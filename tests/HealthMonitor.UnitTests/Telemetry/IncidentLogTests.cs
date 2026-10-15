
using System;
using HealthMonitor.Domain.Telemetry;
using Xunit;

namespace HealthMonitor.UnitTests.Telemetry;

/// <summary>
/// Verifies the domain invariants and lifecycle behavior
/// of the IncidentLog entity.
/// </summary>
/// <remarks>
/// Tests cover incident creation, identifier validation,
/// supported incident kinds, summary normalization,
/// UTC timestamps, resolution chronology, and repeated
/// resolution attempts.
///
/// Tests also verify that failed resolution operations
/// preserve the existing incident state.
///
/// These are isolated domain unit tests requiring no
/// EF Core, PostgreSQL, or external services.
/// </remarks>
public sealed class IncidentLogTests
{
    // Fixed timestamps make lifecycle and boundary tests
    // deterministic and independent of the system clock.
    private static readonly DateTimeOffset OpenedAt =
        new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ResolvedAt =
        OpenedAt.AddMinutes(5);

    /// <summary>
    /// Creates an unresolved incident using valid default values.
    /// </summary>
    /// <param name="id">
    /// Optional incident identifier. Null generates a new GUID;
    /// Guid.Empty is forwarded for domain validation.
    /// </param>
    /// <param name="deviceId">
    /// Optional device identifier. Null generates a new GUID;
    /// Guid.Empty is forwarded for domain validation.
    /// </param>
    /// <param name="kind">Incident category to validate.</param>
    /// <param name="openedAtUtc">
    /// Optional opening timestamp, defaulting to the fixed test time.
    /// </param>
    /// <param name="openedByPingId">
    /// Optional opening observation identifier. Null generates
    /// a GUID; Guid.Empty is forwarded for validation.
    /// </param>
    /// <param name="summary">Incident summary to validate.</param>
    /// <returns>A validated unresolved incident.</returns>
    /// <remarks>
    /// Tests override only the inputs relevant to their scenarios,
    /// avoiding repeated setup code.
    ///
    /// Nullable GUID parameters distinguish omitted identifiers
    /// from explicitly invalid Guid.Empty values:
    /// - Null generates a new GUID.
    /// - Guid.Empty is forwarded unchanged for domain validation.
    ///
    /// The helper performs no domain validation or normalization;
    /// those responsibilities remain with IncidentLog.Open.
    ///
    /// To test an explicitly supplied default DateTimeOffset,
    /// invoke IncidentLog.Open directly because null selects
    /// the helper's fixed default timestamp.
    /// </remarks>
    private static IncidentLog CreateIncident(
        Guid? id = null,
        Guid? deviceId = null,
        IncidentKind kind = IncidentKind.Latency,
        DateTimeOffset? openedAtUtc = null,
        Guid? openedByPingId = null,
        string summary = "High latency detected")
    {
        return IncidentLog.Open(
            id ?? Guid.NewGuid(),
            deviceId ?? Guid.NewGuid(),
            kind,
            openedAtUtc ?? OpenedAt,
            openedByPingId ?? Guid.NewGuid(),
            summary);
    }

    [Fact]
    public void Open_ValidValues_CreatesUnresolvedIncident()
    {
        Guid id = Guid.NewGuid();
        Guid deviceId = Guid.NewGuid();
        Guid pingId = Guid.NewGuid();

        IncidentLog incident = CreateIncident(
            id: id,
            deviceId: deviceId,
            openedByPingId: pingId);

        Assert.Equal(id, incident.Id);
        Assert.Equal(deviceId, incident.DeviceId);
        Assert.Equal(IncidentKind.Latency, incident.Kind);
        Assert.Equal(OpenedAt, incident.OpenedAtUtc);
        Assert.Equal(pingId, incident.OpenedByPingId);
        Assert.Equal("High latency detected", incident.Summary);
        Assert.Null(incident.ResolvedAtUtc);
        Assert.Null(incident.ResolvedByPingId);
    }

    [Theory]
    [InlineData(IncidentKind.Latency)]
    [InlineData(IncidentKind.Cpu)]
    [InlineData(IncidentKind.Connectivity)]
    public void Open_DefinedIncidentKind_IsAccepted(
        IncidentKind kind)
    {
        IncidentLog incident = CreateIncident(kind: kind);

        Assert.Equal(kind, incident.Kind);
    }

    [Fact]
    public void Open_UndefinedIncidentKind_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateIncident(kind: (IncidentKind)999));
    }

    [Theory]
    [InlineData("  High latency detected  ", "High latency detected")]
    [InlineData(" CPU warning ", "CPU warning")]
    [InlineData("Connectivity lost", "Connectivity lost")]
    public void Open_ValidSummary_IsTrimmed(
        string input,
        string expected)
    {
        IncidentLog incident = CreateIncident(summary: input);

        Assert.Equal(expected, incident.Summary);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Open_InvalidSummary_Throws(string? summary)
    {
        Assert.Throws<ArgumentException>(
            () => CreateIncident(summary: summary!));
    }

    [Fact]
    public void Open_EmptyIncidentId_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => CreateIncident(id: Guid.Empty));
    }

    [Fact]
    public void Open_EmptyDeviceId_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => CreateIncident(deviceId: Guid.Empty));
    }

    [Fact]
    public void Open_EmptyOpeningPingId_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => CreateIncident(openedByPingId: Guid.Empty));
    }

    [Fact]
    public void Open_DefaultTimestamp_Throws()
    {
        // Pass the default timestamp directly because the
        // helper substitutes its fixed time for null.
        Assert.Throws<ArgumentException>(
            () => IncidentLog.Open(
                Guid.NewGuid(),
                Guid.NewGuid(),
                IncidentKind.Latency,
                default(DateTimeOffset),
                Guid.NewGuid(),
                "High latency detected"));
    }

    [Fact]
    public void Open_NonUtcTimestamp_Throws()
    {
        DateTimeOffset nonUtc =
            new(2026, 10, 14, 12, 0, 0, TimeSpan.FromHours(-6));

        Assert.Throws<ArgumentException>(
            () => CreateIncident(openedAtUtc: nonUtc));
    }

    [Fact]
    public void Resolve_ValidValues_SetsResolutionFields()
    {
        IncidentLog incident = CreateIncident();
        Guid recoveryPingId = Guid.NewGuid();

        incident.Resolve(ResolvedAt, recoveryPingId);

        Assert.Equal(ResolvedAt, incident.ResolvedAtUtc);
        Assert.Equal(recoveryPingId, incident.ResolvedByPingId);
    }

    [Fact]
    public void Resolve_AtOpeningTimestamp_IsAllowed()
    {
        IncidentLog incident = CreateIncident();
        Guid recoveryPingId = Guid.NewGuid();

        incident.Resolve(OpenedAt, recoveryPingId);

        Assert.Equal(OpenedAt, incident.ResolvedAtUtc);
        Assert.Equal(recoveryPingId, incident.ResolvedByPingId);
    }

    [Fact]
    public void Resolve_BeforeOpening_ThrowsWithoutChangingState()
    {
        IncidentLog incident = CreateIncident();

        Assert.Throws<ArgumentException>(
            () => incident.Resolve(
                OpenedAt.AddTicks(-1),
                Guid.NewGuid()));

        Assert.Null(incident.ResolvedAtUtc);
        Assert.Null(incident.ResolvedByPingId);
    }

    [Fact]
    public void Resolve_DefaultTimestamp_ThrowsWithoutChangingState()
    {
        IncidentLog incident = CreateIncident();

        Assert.Throws<ArgumentException>(
            () => incident.Resolve(
                default(DateTimeOffset),
                Guid.NewGuid()));

        Assert.Null(incident.ResolvedAtUtc);
        Assert.Null(incident.ResolvedByPingId);
    }

    [Fact]
    public void Resolve_NonUtcTimestamp_ThrowsWithoutChangingState()
    {
        IncidentLog incident = CreateIncident();

        DateTimeOffset nonUtc =
            new(2026, 10, 15, 12, 0, 0, TimeSpan.FromHours(-6));

        Assert.Throws<ArgumentException>(
            () => incident.Resolve(nonUtc, Guid.NewGuid()));

        Assert.Null(incident.ResolvedAtUtc);
        Assert.Null(incident.ResolvedByPingId);
    }

    [Fact]
    public void Resolve_EmptyRecoveryPingId_ThrowsWithoutChangingState()
    {
        IncidentLog incident = CreateIncident();

        Assert.Throws<ArgumentException>(
            () => incident.Resolve(ResolvedAt, Guid.Empty));

        Assert.Null(incident.ResolvedAtUtc);
        Assert.Null(incident.ResolvedByPingId);
    }

    [Fact]
    public void Resolve_RepeatedCall_ThrowsAndPreservesOriginalResolution()
    {
        IncidentLog incident = CreateIncident();
        Guid firstPingId = Guid.NewGuid();

        incident.Resolve(ResolvedAt, firstPingId);

        Assert.Throws<InvalidOperationException>(
            () => incident.Resolve(
                ResolvedAt.AddMinutes(1),
                Guid.NewGuid()));

        Assert.Equal(ResolvedAt, incident.ResolvedAtUtc);
        Assert.Equal(firstPingId, incident.ResolvedByPingId);
    }

    [Fact]
    public void Resolve_AlreadyResolved_InvalidTimestamp_ThrowsArgumentException()
    {
        IncidentLog incident = CreateIncident();
        Guid firstPingId = Guid.NewGuid();

        incident.Resolve(ResolvedAt, firstPingId);

        // Current domain behavior validates arguments before
        // checking whether the incident is already resolved.
        Assert.Throws<ArgumentException>(
            () => incident.Resolve(
                default(DateTimeOffset),
                Guid.NewGuid()));

        Assert.Equal(ResolvedAt, incident.ResolvedAtUtc);
        Assert.Equal(firstPingId, incident.ResolvedByPingId);
    }

    [Fact]
    public void Resolve_AlreadyResolved_EmptyPingId_ThrowsArgumentException()
    {
        IncidentLog incident = CreateIncident();
        Guid firstPingId = Guid.NewGuid();

        incident.Resolve(ResolvedAt, firstPingId);

        Assert.Throws<ArgumentException>(
            () => incident.Resolve(
                ResolvedAt.AddMinutes(1),
                Guid.Empty));

        Assert.Equal(ResolvedAt, incident.ResolvedAtUtc);
        Assert.Equal(firstPingId, incident.ResolvedByPingId);
    }

    [Fact]
    public void Open_SeparateCalls_CreateIndependentIncidents()
    {
        IncidentLog first = CreateIncident();
        IncidentLog second = CreateIncident();

        Assert.NotEqual(first.Id, second.Id);
    }

    /// <summary>
    /// Verifies the current incident summary length contract.
    /// </summary>
    /// <remarks>
    /// A single non-whitespace character is valid.
    ///
    /// The domain currently does not enforce a maximum summary length.
    /// This test documents that behavior; it does not establish an
    /// application-level recommendation for unbounded input.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(5000)]
    public void Open_ValidSummaryLength_IsAccepted(int length)
    {
        string summary = new('A', length);

        IncidentLog incident = CreateIncident(summary: summary);

        Assert.Equal(summary, incident.Summary);
        Assert.Equal(length, incident.Summary.Length);
    }    
}
