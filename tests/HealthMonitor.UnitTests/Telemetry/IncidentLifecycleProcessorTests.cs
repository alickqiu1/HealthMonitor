
using System;
using System.Collections.Generic;
using System.Linq;
using HealthMonitor.Domain.Telemetry;
using Xunit;

namespace HealthMonitor.UnitTests.Telemetry;

/// <summary>
/// Verifies the incident lifecycle transitions performed by
/// IncidentLifecycleProcessor for a monitored device.
/// </summary>
/// <remarks>
/// Tests cover incident opening, preservation of existing incidents,
/// independent incident kinds, successful recovery, timeout behavior,
/// and retention of resolved incident history.
///
/// Error tests cover null arguments, foreign-device incidents,
/// duplicate open incidents, invalid generated identifiers,
/// conflicting identifiers, and invalid resolution chronology.
///
/// These tests operate entirely on in-memory domain entities.
/// They do not verify database transactions, concurrency controls,
/// or PostgreSQL uniqueness constraints.
/// </remarks>
public sealed class IncidentLifecycleProcessorTests
{
    // Fixed UTC timestamps make incident chronology and boundary
    // scenarios deterministic and independent of wall-clock time.
    private static readonly DateTimeOffset InitialTime =
        new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset LaterTime =
        InitialTime.AddMinutes(1);

    private readonly IncidentLifecycleProcessor _processor = new();

    /// <summary>
    /// Creates a successful observation with valid defaults.
    /// </summary>
    /// <param name="deviceId">
    /// Optional device identifier. Null generates a new GUID;
    /// Guid.Empty is forwarded for domain validation.
    /// </param>
    /// <param name="observedAtUtc">
    /// Optional UTC observation time, defaulting to InitialTime.
    /// </param>
    /// <param name="latencyMs">Synthetic latency in milliseconds.</param>
    /// <param name="cpuPercent">Synthetic CPU utilization percentage.</param>
    /// <returns>A validated successful observation.</returns>
    /// <remarks>
    /// Each invocation creates independent ping and batch IDs.
    /// Validation belongs to the TelemetryPing factory.
    /// </remarks>
    private static TelemetryPing CreateSuccess(
        Guid? deviceId = null,
        DateTimeOffset? observedAtUtc = null,
        int latencyMs = 100,
        decimal cpuPercent = 25m)
    {
        return TelemetryPing.CreateSuccess(
            Guid.NewGuid(),
            deviceId ?? Guid.NewGuid(),
            Guid.NewGuid(),
            observedAtUtc ?? InitialTime,
            latencyMs,
            cpuPercent);
    }

    /// <summary>
    /// Creates a timeout observation with valid defaults.
    /// </summary>
    /// <param name="deviceId">
    /// Optional device identifier. Null generates a new GUID;
    /// Guid.Empty is forwarded for domain validation.
    /// </param>
    /// <param name="observedAtUtc">
    /// Optional UTC observation time, defaulting to InitialTime.
    /// </param>
    /// <returns>A validated timeout observation.</returns>
    private static TelemetryPing CreateTimeout(
        Guid? deviceId = null,
        DateTimeOffset? observedAtUtc = null)
    {
        return TelemetryPing.CreateTimeout(
            Guid.NewGuid(),
            deviceId ?? Guid.NewGuid(),
            Guid.NewGuid(),
            observedAtUtc ?? InitialTime);
    }

    /// <summary>
    /// Creates an existing incident for the specified device and kind.
    /// </summary>
    /// <param name="deviceId">Affected device identifier.</param>
    /// <param name="kind">Incident category.</param>
    /// <param name="openedAtUtc">
    /// UTC opening timestamp, defaulting to InitialTime.
    /// </param>
    /// <returns>A new unresolved incident.</returns>
    private static IncidentLog CreateIncident(
        Guid deviceId,
        IncidentKind kind,
        DateTimeOffset? openedAtUtc = null)
    {
        return IncidentLog.Open(
            Guid.NewGuid(),
            deviceId,
            kind,
            openedAtUtc ?? InitialTime,
            Guid.NewGuid(),
            "Existing incident");
    }

    /// <summary>
    /// Captures the lifecycle fields of existing incident entities.
    /// </summary>
    /// <param name="incidents">Incident collection to capture.</param>
    /// <returns>
    /// An ordered snapshot containing identity, category, opening,
    /// and resolution information for each incident.
    /// </returns>
    /// <remarks>
    /// The snapshot is used to verify that rejected processing
    /// operations do not partially modify existing incidents.
    /// </remarks>
    private static (
        Guid Id,
        Guid DeviceId,
        IncidentKind Kind,
        DateTimeOffset OpenedAtUtc,
        DateTimeOffset? ResolvedAtUtc,
        Guid? OpenedByPingId,
        Guid? ResolvedByPingId,
        string Summary
    )[] Capture(IList<IncidentLog> incidents)
    {
        return incidents.Select(i => (
            i.Id,
            i.DeviceId,
            i.Kind,
            i.OpenedAtUtc,
            i.ResolvedAtUtc,
            i.OpenedByPingId,
            i.ResolvedByPingId,
            i.Summary
        )).ToArray();
    }

    /// <summary>
    /// Verifies that incident count, ordering, and all observable
    /// fields remain unchanged after a rejected operation.
    /// </summary>
    /// <param name="incidents">Current incident collection.</param>
    /// <param name="expected">Previously captured snapshot.</param>
    private static void AssertUnchanged(
        IList<IncidentLog> incidents,
        (
            Guid Id,
            Guid DeviceId,
            IncidentKind Kind,
            DateTimeOffset OpenedAtUtc,
            DateTimeOffset? ResolvedAtUtc,
            Guid? OpenedByPingId,
            Guid? ResolvedByPingId,
            string Summary
        )[] expected)
    {
        Assert.Equal(expected.Length, incidents.Count);
        Assert.Equal(expected, Capture(incidents));
    }

    [Fact]
    public void Process_HighLatency_OpensLatencyIncident()
    {
        TelemetryPing ping = CreateSuccess(latencyMs: 251);
        var incidents = new List<IncidentLog>();

        _processor.Process(ping, incidents, Guid.NewGuid);

        IncidentLog incident = Assert.Single(incidents);

        Assert.Equal(ping.DeviceId, incident.DeviceId);
        Assert.Equal(IncidentKind.Latency, incident.Kind);
        Assert.Equal(ping.ObservedAtUtc, incident.OpenedAtUtc);
        Assert.Equal(ping.Id, incident.OpenedByPingId);
        Assert.Null(incident.ResolvedAtUtc);
        Assert.Null(incident.ResolvedByPingId);
        Assert.Equal("High latency detected", incident.Summary);
    }

    [Fact]
    public void Process_HighCpu_OpensCpuIncident()
    {
        TelemetryPing ping = CreateSuccess(cpuPercent: 80.01m);
        var incidents = new List<IncidentLog>();

        _processor.Process(ping, incidents, Guid.NewGuid);

        IncidentLog incident = Assert.Single(incidents);

        Assert.Equal(IncidentKind.Cpu, incident.Kind);
        Assert.Equal(ping.Id, incident.OpenedByPingId);
        Assert.Equal("High CPU utilization detected", incident.Summary);
    }

    [Fact]
    public void Process_Timeout_OpensConnectivityIncident()
    {
        TelemetryPing ping = CreateTimeout();
        var incidents = new List<IncidentLog>();

        _processor.Process(ping, incidents, Guid.NewGuid);

        IncidentLog incident = Assert.Single(incidents);

        Assert.Equal(IncidentKind.Connectivity, incident.Kind);
        Assert.Equal(ping.Id, incident.OpenedByPingId);
        Assert.Equal("Device connectivity timeout detected", incident.Summary);
    }

    [Fact]
    public void Process_HighLatencyAndCpu_OpensTwoIndependentIncidents()
    {
        TelemetryPing ping = CreateSuccess(
            latencyMs: 500,
            cpuPercent: 95m);

        var incidents = new List<IncidentLog>();

        _processor.Process(ping, incidents, Guid.NewGuid);

        Assert.Equal(2, incidents.Count);
        Assert.Contains(incidents, i => i.Kind == IncidentKind.Latency);
        Assert.Contains(incidents, i => i.Kind == IncidentKind.Cpu);
        Assert.All(incidents, i =>
        {
            Assert.Equal(ping.DeviceId, i.DeviceId);
            Assert.Equal(ping.Id, i.OpenedByPingId);
        });

        Assert.NotEqual(incidents[0].Id, incidents[1].Id);
    }

    [Fact]
    public void Process_HealthyObservation_DoesNotOpenIncidents()
    {
        TelemetryPing ping = CreateSuccess(
            latencyMs: 250,
            cpuPercent: 80m);

        var incidents = new List<IncidentLog>();
        int generatedCount = 0;

        _processor.Process(
            ping,
            incidents,
            () =>
            {
                generatedCount++;
                return Guid.NewGuid();
            });

        Assert.Empty(incidents);
        Assert.Equal(0, generatedCount);
    }

    [Fact]
    public void Process_RepeatedHighLatency_PreservesExistingOpenIncident()
    {
        Guid deviceId = Guid.NewGuid();

        IncidentLog existing = CreateIncident(
            deviceId,
            IncidentKind.Latency);

        var incidents = new List<IncidentLog> { existing };

        TelemetryPing ping = CreateSuccess(
            deviceId: deviceId,
            observedAtUtc: LaterTime,
            latencyMs: 400);

        var snapshot = Capture(incidents);
        int generatedCount = 0;

        _processor.Process(
            ping,
            incidents,
            () =>
            {
                generatedCount++;
                return Guid.NewGuid();
            });

        Assert.Equal(0, generatedCount);
        AssertUnchanged(incidents, snapshot);
        Assert.Same(existing, Assert.Single(incidents));
    }

    [Fact]
    public void Process_RepeatedTimeout_PreservesConnectivityIncident()
    {
        Guid deviceId = Guid.NewGuid();

        IncidentLog existing = CreateIncident(
            deviceId,
            IncidentKind.Connectivity);

        var incidents = new List<IncidentLog> { existing };

        TelemetryPing ping = CreateTimeout(
            deviceId: deviceId,
            observedAtUtc: LaterTime);

        var snapshot = Capture(incidents);

        _processor.Process(ping, incidents, Guid.NewGuid);

        AssertUnchanged(incidents, snapshot);
    }

    [Fact]
    public void Process_HealthyObservation_ResolvesAllOpenIncidents()
    {
        Guid deviceId = Guid.NewGuid();

        var incidents = new List<IncidentLog>
        {
            CreateIncident(deviceId, IncidentKind.Latency),
            CreateIncident(deviceId, IncidentKind.Cpu),
            CreateIncident(deviceId, IncidentKind.Connectivity)
        };

        TelemetryPing recovery = CreateSuccess(
            deviceId: deviceId,
            observedAtUtc: LaterTime,
            latencyMs: 250,
            cpuPercent: 80m);

        _processor.Process(recovery, incidents, Guid.NewGuid);

        Assert.Equal(3, incidents.Count);

        Assert.All(incidents, incident =>
        {
            Assert.Equal(LaterTime, incident.ResolvedAtUtc);
            Assert.Equal(recovery.Id, incident.ResolvedByPingId);
        });
    }

    [Fact]
    public void Process_Timeout_PreservesLatencyAndCpuIncidents()
    {
        Guid deviceId = Guid.NewGuid();

        IncidentLog latency = CreateIncident(
            deviceId,
            IncidentKind.Latency);

        IncidentLog cpu = CreateIncident(
            deviceId,
            IncidentKind.Cpu);

        var incidents = new List<IncidentLog> { latency, cpu };

        TelemetryPing timeout = CreateTimeout(
            deviceId: deviceId,
            observedAtUtc: LaterTime);

        _processor.Process(timeout, incidents, Guid.NewGuid);

        Assert.Equal(3, incidents.Count);

        Assert.Null(latency.ResolvedAtUtc);
        Assert.Null(cpu.ResolvedAtUtc);

        IncidentLog connectivity = Assert.Single(
            incidents,
            i => i.Kind == IncidentKind.Connectivity);

        Assert.Null(connectivity.ResolvedAtUtc);
        Assert.Equal(timeout.Id, connectivity.OpenedByPingId);
    }

    [Fact]
    public void Process_Success_ResolvesConnectivityButKeepsHighLatencyOpen()
    {
        Guid deviceId = Guid.NewGuid();

        IncidentLog connectivity = CreateIncident(
            deviceId,
            IncidentKind.Connectivity);

        IncidentLog latency = CreateIncident(
            deviceId,
            IncidentKind.Latency);

        var incidents = new List<IncidentLog>
        {
            connectivity,
            latency
        };

        TelemetryPing recovery = CreateSuccess(
            deviceId: deviceId,
            observedAtUtc: LaterTime,
            latencyMs: 400,
            cpuPercent: 25m);

        _processor.Process(recovery, incidents, Guid.NewGuid);

        Assert.Equal(LaterTime, connectivity.ResolvedAtUtc);
        Assert.Equal(recovery.Id, connectivity.ResolvedByPingId);
        Assert.Null(latency.ResolvedAtUtc);
    }

    [Fact]
    public void Process_ResolvedHistoricalIncident_AllowsNewIncidentOfSameKind()
    {
        Guid deviceId = Guid.NewGuid();

        IncidentLog historical = CreateIncident(
            deviceId,
            IncidentKind.Latency);

        historical.Resolve(
            InitialTime.AddSeconds(10),
            Guid.NewGuid());

        var incidents = new List<IncidentLog> { historical };

        TelemetryPing ping = CreateSuccess(
            deviceId: deviceId,
            observedAtUtc: LaterTime,
            latencyMs: 300);

        _processor.Process(ping, incidents, Guid.NewGuid);

        Assert.Equal(2, incidents.Count);

        IncidentLog newlyOpened = Assert.Single(
            incidents,
            i => !i.ResolvedAtUtc.HasValue);

        Assert.Equal(IncidentKind.Latency, newlyOpened.Kind);
        Assert.Equal(ping.Id, newlyOpened.OpenedByPingId);
        Assert.Equal(
            InitialTime.AddSeconds(10),
            historical.ResolvedAtUtc);
    }

    [Fact]
    public void Process_NullPing_ThrowsWithoutChangingIncidents()
    {
        var incidents = new List<IncidentLog>();
        var snapshot = Capture(incidents);

        Assert.Throws<ArgumentNullException>(
            () => _processor.Process(null!, incidents, Guid.NewGuid));

        AssertUnchanged(incidents, snapshot);
    }

    [Fact]
    public void Process_NullIncidentCollection_Throws()
    {
        TelemetryPing ping = CreateSuccess();

        Assert.Throws<ArgumentNullException>(
            () => _processor.Process(
                ping,
                null!,
                Guid.NewGuid));
    }

    [Fact]
    public void Process_NullIdFactory_ThrowsWithoutChangingIncidents()
    {
        TelemetryPing ping = CreateSuccess(latencyMs: 400);
        var incidents = new List<IncidentLog>();
        var snapshot = Capture(incidents);

        Assert.Throws<ArgumentNullException>(
            () => _processor.Process(ping, incidents, null!));

        AssertUnchanged(incidents, snapshot);
    }

    [Fact]
    public void Process_DifferentDeviceIncident_ThrowsWithoutMutation()
    {
        TelemetryPing ping = CreateSuccess();

        var incidents = new List<IncidentLog>
        {
            CreateIncident(
                Guid.NewGuid(),
                IncidentKind.Latency)
        };

        var snapshot = Capture(incidents);

        Assert.Throws<ArgumentException>(
            () => _processor.Process(ping, incidents, Guid.NewGuid));

        AssertUnchanged(incidents, snapshot);
    }

    [Fact]
    public void Process_DuplicateOpenIncidents_ThrowsWithoutMutation()
    {
        Guid deviceId = Guid.NewGuid();

        var incidents = new List<IncidentLog>
        {
            CreateIncident(deviceId, IncidentKind.Latency),
            CreateIncident(deviceId, IncidentKind.Latency)
        };

        TelemetryPing ping = CreateSuccess(
            deviceId: deviceId,
            observedAtUtc: LaterTime,
            latencyMs: 400);

        var snapshot = Capture(incidents);

        Assert.Throws<InvalidOperationException>(
            () => _processor.Process(ping, incidents, Guid.NewGuid));

        AssertUnchanged(incidents, snapshot);
    }

    [Fact]
    public void Process_EmptyGeneratedId_ThrowsWithoutMutation()
    {
        TelemetryPing ping = CreateSuccess(latencyMs: 400);
        var incidents = new List<IncidentLog>();
        var snapshot = Capture(incidents);

        Assert.Throws<InvalidOperationException>(
            () => _processor.Process(
                ping,
                incidents,
                () => Guid.Empty));

        AssertUnchanged(incidents, snapshot);
    }

    [Fact]
    public void Process_GeneratedIdConflictsWithHistoricalIncident_Throws()
    {
        Guid deviceId = Guid.NewGuid();

        IncidentLog historical = CreateIncident(
            deviceId,
            IncidentKind.Cpu);

        historical.Resolve(
            InitialTime.AddSeconds(10),
            Guid.NewGuid());

        var incidents = new List<IncidentLog> { historical };

        TelemetryPing ping = CreateSuccess(
            deviceId: deviceId,
            observedAtUtc: LaterTime,
            latencyMs: 400);

        var snapshot = Capture(incidents);

        Assert.Throws<InvalidOperationException>(
            () => _processor.Process(
                ping,
                incidents,
                () => historical.Id));

        AssertUnchanged(incidents, snapshot);
    }

    [Fact]
    public void Process_GeneratedIdsConflictWithinBatch_ThrowsWithoutMutation()
    {
        Guid generatedId = Guid.NewGuid();

        TelemetryPing ping = CreateSuccess(
            latencyMs: 400,
            cpuPercent: 90m);

        var incidents = new List<IncidentLog>();
        var snapshot = Capture(incidents);

        Assert.Throws<InvalidOperationException>(
            () => _processor.Process(
                ping,
                incidents,
                () => generatedId));

        AssertUnchanged(incidents, snapshot);
    }

    [Fact]
    public void Process_RecoveryBeforeIncidentOpening_ThrowsWithoutMutation()
    {
        Guid deviceId = Guid.NewGuid();

        IncidentLog incident = CreateIncident(
            deviceId,
            IncidentKind.Latency,
            openedAtUtc: LaterTime);

        var incidents = new List<IncidentLog> { incident };

        TelemetryPing earlierRecovery = CreateSuccess(
            deviceId: deviceId,
            observedAtUtc: InitialTime,
            latencyMs: 100);

        var snapshot = Capture(incidents);

        Assert.Throws<InvalidOperationException>(
            () => _processor.Process(
                earlierRecovery,
                incidents,
                Guid.NewGuid));

        AssertUnchanged(incidents, snapshot);
    }

    [Fact]
    public void Process_RecoveryAtIncidentOpeningTime_IsAllowed()
    {
        Guid deviceId = Guid.NewGuid();

        IncidentLog incident = CreateIncident(
            deviceId,
            IncidentKind.Latency);

        var incidents = new List<IncidentLog> { incident };

        TelemetryPing recovery = CreateSuccess(
            deviceId: deviceId,
            observedAtUtc: InitialTime,
            latencyMs: 250);

        _processor.Process(recovery, incidents, Guid.NewGuid);

        Assert.Equal(InitialTime, incident.ResolvedAtUtc);
        Assert.Equal(recovery.Id, incident.ResolvedByPingId);
    }

    [Fact]
    public void Process_HealthyObservation_LeavesResolvedHistoryUnchanged()
    {
        Guid deviceId = Guid.NewGuid();

        IncidentLog historical = CreateIncident(
            deviceId,
            IncidentKind.Connectivity);

        historical.Resolve(
            InitialTime.AddSeconds(10),
            Guid.NewGuid());

        var incidents = new List<IncidentLog> { historical };

        TelemetryPing ping = CreateSuccess(
            deviceId: deviceId,
            observedAtUtc: LaterTime);

        var snapshot = Capture(incidents);

        _processor.Process(ping, incidents, Guid.NewGuid);

        AssertUnchanged(incidents, snapshot);
    }
}
