using System.Globalization;
using HealthMonitor.Domain.Telemetry;

namespace HealthMonitor.UnitTests.Telemetry;

/// <summary>Checks pure health priority, exact thresholds, valid absence, and existing observation invariants without a clock or database.</summary>
public sealed class TelemetryHealthPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    /// <summary>Parses decimal test inputs independently of machine culture and attribute decimal restrictions.</summary>
    private static decimal Cpu(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    [Fact]
    public void ValidAbsence_ReturnsUnknown() => Assert.Equal(HealthState.Unknown, TelemetryHealthPolicy.Classify(Now, null, null, null, null));

    [Theory]
    [InlineData(0, "0", HealthState.Healthy)]
    [InlineData(249, "79.99", HealthState.Healthy)]
    [InlineData(250, "80", HealthState.Healthy)]
    [InlineData(251, "80", HealthState.Degraded)]
    [InlineData(250, "80.01", HealthState.Degraded)]
    [InlineData(251, "80.01", HealthState.Degraded)]
    [InlineData(60000, "100", HealthState.Degraded)]
    public void FreshSuccess_UsesExactMeasurementBoundaries(int latency, string cpu, HealthState expected) =>
        Assert.Equal(expected, TelemetryHealthPolicy.Classify(Now, Now, PingOutcome.Success, latency, Cpu(cpu)));

    [Theory]
    [InlineData(-1, HealthState.Healthy)]
    [InlineData(0, HealthState.Healthy)]
    [InlineData(1, HealthState.Offline)]
    public void AgeBoundary_OneTickBelowExactAndAboveNinetySeconds(long deltaTicks, HealthState expected)
    {
        var age = TimeSpan.FromSeconds(90) + TimeSpan.FromTicks(deltaTicks);
        Assert.Equal(expected, TelemetryHealthPolicy.Classify(Now, Now - age, PingOutcome.Success, 250, 80m));
    }

    [Fact]
    public void Staleness_TakesPriorityOverDegradedMeasurements() =>
        Assert.Equal(HealthState.Offline, TelemetryHealthPolicy.Classify(Now, Now.AddSeconds(-91), PingOutcome.Success, 60000, 100m));

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(91)]
    public void Timeout_IsOfflineWhetherFreshOrStale(int ageSeconds) =>
        Assert.Equal(HealthState.Offline, TelemetryHealthPolicy.Classify(Now, Now.AddSeconds(-ageSeconds), PingOutcome.Timeout, null, null));

    [Theory]
    [InlineData(PingOutcome.Success, HealthState.Healthy)]
    [InlineData(PingOutcome.Timeout, HealthState.Offline)]
    public void FutureObservation_FollowsLiteralAgeComparison(PingOutcome outcome, HealthState expected) =>
        Assert.Equal(expected, TelemetryHealthPolicy.Classify(Now, Now.AddMinutes(1), outcome,
            outcome == PingOutcome.Success ? 40 : null, outcome == PingOutcome.Success ? 30m : null));

    [Theory]
    [InlineData(0, null, null)]
    [InlineData(1, null, null)]
    [InlineData(null, 0, null)]
    [InlineData(null, null, "0")]
    public void AbsentObservation_WithOtherFields_IsRejected(int? outcome, int? latency, string? cpu) =>
        Assert.Throws<ArgumentException>(() => TelemetryHealthPolicy.Classify(Now, null,
            outcome.HasValue ? (PingOutcome)outcome.Value : null, latency, cpu is null ? null : Cpu(cpu)));

    [Fact]
    public void Observation_WithoutOutcome_IsRejected() =>
        Assert.Throws<ArgumentException>(() => TelemetryHealthPolicy.Classify(Now, Now, null, 40, 30m));

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void UnknownOutcome_IsRejected(int outcome) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TelemetryHealthPolicy.Classify(Now, Now, (PingOutcome)outcome, null, null));

    [Theory]
    [InlineData(null, "30")]
    [InlineData(40, null)]
    [InlineData(null, null)]
    public void Success_WithMissingMeasurement_IsRejected(int? latency, string? cpu) =>
        Assert.Throws<ArgumentException>(() => TelemetryHealthPolicy.Classify(Now, Now, PingOutcome.Success, latency, cpu is null ? null : Cpu(cpu)));

    [Theory]
    [InlineData(0, null)]
    [InlineData(null, "0")]
    [InlineData(40, "30")]
    public void Timeout_WithMeasurements_IsRejected(int? latency, string? cpu) =>
        Assert.Throws<ArgumentException>(() => TelemetryHealthPolicy.Classify(Now, Now, PingOutcome.Timeout, latency, cpu is null ? null : Cpu(cpu)));

    [Theory]
    [InlineData(-1)]
    [InlineData(60001)]
    [InlineData(int.MaxValue)]
    public void LatencyOutsideExistingDomainRange_IsRejected(int latency) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TelemetryHealthPolicy.Classify(Now, Now, PingOutcome.Success, latency, 30m));

    [Theory]
    [InlineData("-0.01")]
    [InlineData("100.01")]
    [InlineData("80.001")]
    public void CpuRangeOrPrecisionViolation_IsRejected(string cpu) =>
        Assert.ThrowsAny<ArgumentException>(() => TelemetryHealthPolicy.Classify(Now, Now, PingOutcome.Success, 40, Cpu(cpu)));

    [Theory]
    [InlineData("asof_default")]
    [InlineData("observation_default")]
    [InlineData("asof_offset")]
    [InlineData("observation_offset")]
    public void InvalidTimestamps_AreRejected(string field)
    {
        var asOf = field == "asof_default" ? default : field == "asof_offset" ? Now.ToOffset(TimeSpan.FromHours(1)) : Now;
        var observation = field == "observation_default" ? default : field == "observation_offset" ? Now.ToOffset(TimeSpan.FromHours(-1)) : Now;
        Assert.Throws<ArgumentException>(() => TelemetryHealthPolicy.Classify(asOf, observation, PingOutcome.Success, 40, 30m));
    }

    [Fact]
    public void InvalidEvaluationTime_IsRejectedEvenForAbsentObservation() =>
        Assert.Throws<ArgumentException>(() => TelemetryHealthPolicy.Classify(default, null, null, null, null));

    [Fact]
    public void MalformedStaleObservation_IsRejectedRatherThanHiddenByOffline() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TelemetryHealthPolicy.Classify(Now, Now.AddDays(-2), PingOutcome.Success, -1, 30m));
}
