
using System;
using HealthMonitor.Domain.Telemetry;
using Xunit;

namespace HealthMonitor.UnitTests.Telemetry;

/// <summary>
/// Verifies the initialization and lifecycle invariants
/// of the DeviceTelemetryState entity.
/// </summary>
/// <remarks>
/// Tests cover initialization from successful and timeout
/// observations, subsequent updates, timeout recovery,
/// observation ordering, duplicate observation rejection,
/// and device ownership validation.
///
/// Tests also verify that invalid updates preserve the
/// previously recorded state and that timeout observations
/// retain the last successful observation timestamp.
///
/// These are isolated domain unit tests requiring no
/// EF Core, PostgreSQL, or external services.
/// </remarks>
public sealed class DeviceTelemetryStateTests
{
    // Fixed observation times make ordering and lifecycle
    // tests deterministic and independent of the system clock.
    private static readonly DateTimeOffset InitialTime =
        new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset LaterTime =
        InitialTime.AddMinutes(1);

    /// <summary>
    /// Creates a successful observation with valid defaults.
    /// </summary>
    /// <param name="deviceId">
    /// Optional device identifier. Null generates a new GUID;
    /// Guid.Empty is forwarded for domain validation.
    /// </param>
    /// <param name="id">
    /// Optional ping identifier. Null generates a new GUID;
    /// Guid.Empty is forwarded for domain validation.
    /// </param>
    /// <param name="observedAtUtc">
    /// Optional observation time, defaulting to InitialTime.
    /// </param>
    /// <param name="latencyMs">Latency in milliseconds.</param>
    /// <param name="cpuPercent">CPU utilization percentage.</param>
    /// <returns>A validated successful observation.</returns>
    /// <remarks>
    /// A new batch identifier is generated for each observation.
    /// The helper does not perform domain validation; it delegates
    /// validation to TelemetryPing.CreateSuccess.
    ///
    /// To test an explicitly invalid default timestamp, invoke
    /// the domain factory directly because null selects InitialTime.
    /// </remarks>
    private static TelemetryPing CreateSuccess(
        Guid? deviceId = null,
        Guid? id = null,
        DateTimeOffset? observedAtUtc = null,
        int latencyMs = 100,
        decimal cpuPercent = 25.50m)
    {
        return TelemetryPing.CreateSuccess(
            id ?? Guid.NewGuid(),
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
    /// <param name="id">
    /// Optional ping identifier. Null generates a new GUID;
    /// Guid.Empty is forwarded for domain validation.
    /// </param>
    /// <param name="observedAtUtc">
    /// Optional observation time, defaulting to InitialTime.
    /// </param>
    /// <returns>A validated timeout observation.</returns>
    /// <remarks>
    /// The timeout factory does not accept latency or CPU values.
    /// Each call generates a new simulation batch identifier.
    /// </remarks>
    private static TelemetryPing CreateTimeout(
        Guid? deviceId = null,
        Guid? id = null,
        DateTimeOffset? observedAtUtc = null)
    {
        return TelemetryPing.CreateTimeout(
            id ?? Guid.NewGuid(),
            deviceId ?? Guid.NewGuid(),
            Guid.NewGuid(),
            observedAtUtc ?? InitialTime);
    }

    /// <summary>
    /// Verifies that all observable state fields match
    /// their previously captured values.
    /// </summary>
    /// <param name="state">State entity to inspect.</param>
    /// <param name="snapshot">Expected state snapshot.</param>
    /// <remarks>
    /// This helper is used after rejected updates to confirm
    /// that validation failures do not partially modify state.
    /// </remarks>
    private static void AssertUnchanged(
        DeviceTelemetryState state,
        (
            Guid DeviceId,
            Guid LastPingId,
            DateTimeOffset LastObservedAtUtc,
            DateTimeOffset? LastSuccessfulAtUtc,
            PingOutcome LatestOutcome,
            int? LatestLatencyMs,
            decimal? LatestCpuPercent
        ) snapshot)
    {
        Assert.Equal(snapshot.DeviceId, state.DeviceId);
        Assert.Equal(snapshot.LastPingId, state.LastPingId);
        Assert.Equal(snapshot.LastObservedAtUtc, state.LastObservedAtUtc);
        Assert.Equal(snapshot.LastSuccessfulAtUtc, state.LastSuccessfulAtUtc);
        Assert.Equal(snapshot.LatestOutcome, state.LatestOutcome);
        Assert.Equal(snapshot.LatestLatencyMs, state.LatestLatencyMs);
        Assert.Equal(snapshot.LatestCpuPercent, state.LatestCpuPercent);
    }

    /// <summary>
    /// Captures the complete observable telemetry state.
    /// </summary>
    /// <param name="state">State entity to capture.</param>
    /// <returns>Snapshot of all public state properties.</returns>
    private static (
        Guid DeviceId,
        Guid LastPingId,
        DateTimeOffset LastObservedAtUtc,
        DateTimeOffset? LastSuccessfulAtUtc,
        PingOutcome LatestOutcome,
        int? LatestLatencyMs,
        decimal? LatestCpuPercent
    ) Capture(DeviceTelemetryState state)
    {
        return (
            state.DeviceId,
            state.LastPingId,
            state.LastObservedAtUtc,
            state.LastSuccessfulAtUtc,
            state.LatestOutcome,
            state.LatestLatencyMs,
            state.LatestCpuPercent);
    }

    [Fact]
    public void Create_Success_InitializesAllFields()
    {
        Guid deviceId = Guid.NewGuid();
        TelemetryPing ping = CreateSuccess(
            deviceId: deviceId,
            latencyMs: 150,
            cpuPercent: 42.25m);

        DeviceTelemetryState state = DeviceTelemetryState.Create(ping);

        Assert.Equal(deviceId, state.DeviceId);
        Assert.Equal(ping.Id, state.LastPingId);
        Assert.Equal(InitialTime, state.LastObservedAtUtc);
        Assert.Equal(InitialTime, state.LastSuccessfulAtUtc);
        Assert.Equal(PingOutcome.Success, state.LatestOutcome);
        Assert.Equal(150, state.LatestLatencyMs);
        Assert.Equal(42.25m, state.LatestCpuPercent);
    }

    [Fact]
    public void Create_Timeout_InitializesWithoutMeasurementsOrSuccessTime()
    {
        TelemetryPing ping = CreateTimeout();

        DeviceTelemetryState state = DeviceTelemetryState.Create(ping);

        Assert.Equal(ping.DeviceId, state.DeviceId);
        Assert.Equal(ping.Id, state.LastPingId);
        Assert.Equal(InitialTime, state.LastObservedAtUtc);
        Assert.Null(state.LastSuccessfulAtUtc);
        Assert.Equal(PingOutcome.Timeout, state.LatestOutcome);
        Assert.Null(state.LatestLatencyMs);
        Assert.Null(state.LatestCpuPercent);
    }

    [Fact]
    public void Create_NullPing_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => DeviceTelemetryState.Create(null!));
    }

    [Fact]
    public void ApplyPing_NullPing_ThrowsWithoutChangingState()
    {
        DeviceTelemetryState state =
            DeviceTelemetryState.Create(CreateSuccess());

        var snapshot = Capture(state);

        Assert.Throws<ArgumentNullException>(
            () => state.ApplyPing(null!));

        AssertUnchanged(state, snapshot);
    }

    [Fact]
    public void ApplyPing_DifferentDevice_ThrowsWithoutChangingState()
    {
        Guid deviceId = Guid.NewGuid();

        DeviceTelemetryState state =
            DeviceTelemetryState.Create(
                CreateSuccess(deviceId: deviceId));

        TelemetryPing differentDevicePing = CreateSuccess(
            deviceId: Guid.NewGuid(),
            observedAtUtc: LaterTime);

        var snapshot = Capture(state);

        Assert.Throws<ArgumentException>(
            () => state.ApplyPing(differentDevicePing));

        AssertUnchanged(state, snapshot);
    }

    [Fact]
    public void ApplyPing_Success_UpdatesLatestStateAndSuccessTime()
    {
        Guid deviceId = Guid.NewGuid();

        DeviceTelemetryState state =
            DeviceTelemetryState.Create(
                CreateSuccess(deviceId: deviceId));

        TelemetryPing nextPing = CreateSuccess(
            deviceId: deviceId,
            observedAtUtc: LaterTime,
            latencyMs: 275,
            cpuPercent: 81.25m);

        state.ApplyPing(nextPing);

        Assert.Equal(nextPing.Id, state.LastPingId);
        Assert.Equal(LaterTime, state.LastObservedAtUtc);
        Assert.Equal(LaterTime, state.LastSuccessfulAtUtc);
        Assert.Equal(PingOutcome.Success, state.LatestOutcome);
        Assert.Equal(275, state.LatestLatencyMs);
        Assert.Equal(81.25m, state.LatestCpuPercent);
    }

    [Fact]
    public void ApplyPing_TimeoutAfterSuccess_PreservesLastSuccessfulTime()
    {
        Guid deviceId = Guid.NewGuid();

        DeviceTelemetryState state =
            DeviceTelemetryState.Create(
                CreateSuccess(deviceId: deviceId));

        TelemetryPing timeout = CreateTimeout(
            deviceId: deviceId,
            observedAtUtc: LaterTime);

        state.ApplyPing(timeout);

        Assert.Equal(timeout.Id, state.LastPingId);
        Assert.Equal(LaterTime, state.LastObservedAtUtc);
        Assert.Equal(InitialTime, state.LastSuccessfulAtUtc);
        Assert.Equal(PingOutcome.Timeout, state.LatestOutcome);
        Assert.Null(state.LatestLatencyMs);
        Assert.Null(state.LatestCpuPercent);
    }

    [Fact]
    public void ApplyPing_RepeatedTimeouts_KeepSuccessTimeNull()
    {
        Guid deviceId = Guid.NewGuid();

        DeviceTelemetryState state =
            DeviceTelemetryState.Create(
                CreateTimeout(deviceId: deviceId));

        TelemetryPing secondTimeout = CreateTimeout(
            deviceId: deviceId,
            observedAtUtc: LaterTime);

        state.ApplyPing(secondTimeout);

        Assert.Equal(secondTimeout.Id, state.LastPingId);
        Assert.Equal(LaterTime, state.LastObservedAtUtc);
        Assert.Null(state.LastSuccessfulAtUtc);
        Assert.Equal(PingOutcome.Timeout, state.LatestOutcome);
        Assert.Null(state.LatestLatencyMs);
        Assert.Null(state.LatestCpuPercent);
    }

    [Fact]
    public void ApplyPing_RecoveryAfterTimeout_UpdatesSuccessfulTime()
    {
        Guid deviceId = Guid.NewGuid();

        DeviceTelemetryState state =
            DeviceTelemetryState.Create(
                CreateTimeout(deviceId: deviceId));

        TelemetryPing recovery = CreateSuccess(
            deviceId: deviceId,
            observedAtUtc: LaterTime,
            latencyMs: 80,
            cpuPercent: 15.50m);

        state.ApplyPing(recovery);

        Assert.Equal(recovery.Id, state.LastPingId);
        Assert.Equal(LaterTime, state.LastObservedAtUtc);
        Assert.Equal(LaterTime, state.LastSuccessfulAtUtc);
        Assert.Equal(PingOutcome.Success, state.LatestOutcome);
        Assert.Equal(80, state.LatestLatencyMs);
        Assert.Equal(15.50m, state.LatestCpuPercent);
    }

    [Fact]
    public void ApplyPing_RecoveryAfterSuccessAndTimeout_UpdatesSuccessfulTime()
    {
        Guid deviceId = Guid.NewGuid();

        DeviceTelemetryState state =
            DeviceTelemetryState.Create(
                CreateSuccess(deviceId: deviceId));

        state.ApplyPing(
            CreateTimeout(
                deviceId: deviceId,
                observedAtUtc: LaterTime));

        DateTimeOffset recoveryTime = LaterTime.AddMinutes(1);

        TelemetryPing recovery = CreateSuccess(
            deviceId: deviceId,
            observedAtUtc: recoveryTime,
            latencyMs: 65,
            cpuPercent: 20m);

        state.ApplyPing(recovery);

        Assert.Equal(recovery.Id, state.LastPingId);
        Assert.Equal(recoveryTime, state.LastObservedAtUtc);
        Assert.Equal(recoveryTime, state.LastSuccessfulAtUtc);
        Assert.Equal(PingOutcome.Success, state.LatestOutcome);
        Assert.Equal(65, state.LatestLatencyMs);
        Assert.Equal(20m, state.LatestCpuPercent);
    }

    [Fact]
    public void ApplyPing_SamePing_ThrowsWithoutChangingState()
    {
        TelemetryPing firstPing = CreateSuccess();

        DeviceTelemetryState state =
            DeviceTelemetryState.Create(firstPing);

        var snapshot = Capture(state);

        Assert.Throws<InvalidOperationException>(
            () => state.ApplyPing(firstPing));

        AssertUnchanged(state, snapshot);
    }

    [Fact]
    public void ApplyPing_OlderObservation_ThrowsWithoutChangingState()
    {
        Guid deviceId = Guid.NewGuid();

        DeviceTelemetryState state =
            DeviceTelemetryState.Create(
                CreateSuccess(
                    deviceId: deviceId,
                    observedAtUtc: LaterTime));

        TelemetryPing olderPing = CreateSuccess(
            deviceId: deviceId,
            observedAtUtc: InitialTime);

        var snapshot = Capture(state);

        Assert.Throws<InvalidOperationException>(
            () => state.ApplyPing(olderPing));

        AssertUnchanged(state, snapshot);
    }

    [Fact]
    public void ApplyPing_EqualTimestampDifferentPing_IsAccepted()
    {
        Guid deviceId = Guid.NewGuid();

        DeviceTelemetryState state =
            DeviceTelemetryState.Create(
                CreateSuccess(
                    deviceId: deviceId,
                    latencyMs: 100,
                    cpuPercent: 25m));

        TelemetryPing equalTimePing = CreateSuccess(
            deviceId: deviceId,
            observedAtUtc: InitialTime,
            latencyMs: 200,
            cpuPercent: 50m);

        // Current domain contract permits different observations
        // with equal timestamps; only older timestamps are rejected.
        state.ApplyPing(equalTimePing);

        Assert.Equal(equalTimePing.Id, state.LastPingId);
        Assert.Equal(InitialTime, state.LastObservedAtUtc);
        Assert.Equal(InitialTime, state.LastSuccessfulAtUtc);
        Assert.Equal(200, state.LatestLatencyMs);
        Assert.Equal(50m, state.LatestCpuPercent);
    }

    [Fact]
    public void ApplyPing_EqualTimestampTimeout_PreservesSuccessTime()
    {
        Guid deviceId = Guid.NewGuid();

        DeviceTelemetryState state =
            DeviceTelemetryState.Create(
                CreateSuccess(deviceId: deviceId));

        TelemetryPing timeout = CreateTimeout(
            deviceId: deviceId,
            observedAtUtc: InitialTime);

        state.ApplyPing(timeout);

        Assert.Equal(timeout.Id, state.LastPingId);
        Assert.Equal(InitialTime, state.LastSuccessfulAtUtc);
        Assert.Equal(PingOutcome.Timeout, state.LatestOutcome);
        Assert.Null(state.LatestLatencyMs);
        Assert.Null(state.LatestCpuPercent);
    }

    [Fact]
    public void ApplyPing_NewerPingWithSameId_Throws()
    {
        Guid deviceId = Guid.NewGuid();
        Guid pingId = Guid.NewGuid();

        DeviceTelemetryState state =
            DeviceTelemetryState.Create(
                CreateSuccess(
                    deviceId: deviceId,
                    id: pingId));

        TelemetryPing reusedId = CreateSuccess(
            deviceId: deviceId,
            id: pingId,
            observedAtUtc: LaterTime);

        var snapshot = Capture(state);

        Assert.Throws<InvalidOperationException>(
            () => state.ApplyPing(reusedId));

        AssertUnchanged(state, snapshot);
    }

    [Fact]
    public void ApplyPing_AtMeasurementBoundaries_UpdatesState()
    {
        Guid deviceId = Guid.NewGuid();

        DeviceTelemetryState state =
            DeviceTelemetryState.Create(
                CreateSuccess(deviceId: deviceId));

        TelemetryPing boundaryPing = CreateSuccess(
            deviceId: deviceId,
            observedAtUtc: LaterTime,
            latencyMs: 60000,
            cpuPercent: 100m);

        state.ApplyPing(boundaryPing);

        Assert.Equal(60000, state.LatestLatencyMs);
        Assert.Equal(100m, state.LatestCpuPercent);
        Assert.Equal(LaterTime, state.LastSuccessfulAtUtc);
    }

    /// <summary>
    /// Verifies recovery from a timeout when the recovery observation
    /// has the same timestamp as the currently recorded observation.
    /// </summary>
    /// <remarks>
    /// The current domain implementation permits distinct observations
    /// with equal timestamps.
    ///
    /// A successful observation updates LastSuccessfulAtUtc even
    /// when LastObservedAtUtc does not advance.
    /// </remarks>
    [Fact]
    public void ApplyPing_EqualTimestampSuccessAfterTimeout_UpdatesSuccessfulTime()
    {
        Guid deviceId = Guid.NewGuid();

        DeviceTelemetryState state =
            DeviceTelemetryState.Create(
                CreateTimeout(deviceId: deviceId));

        TelemetryPing recovery = CreateSuccess(
            deviceId: deviceId,
            observedAtUtc: InitialTime,
            latencyMs: 75,
            cpuPercent: 18.50m);

        state.ApplyPing(recovery);

        Assert.Equal(recovery.Id, state.LastPingId);
        Assert.Equal(InitialTime, state.LastObservedAtUtc);
        Assert.Equal(InitialTime, state.LastSuccessfulAtUtc);
        Assert.Equal(PingOutcome.Success, state.LatestOutcome);
        Assert.Equal(75, state.LatestLatencyMs);
        Assert.Equal(18.50m, state.LatestCpuPercent);
    }

}
