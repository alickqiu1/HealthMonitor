
using System;
using HealthMonitor.Application.Telemetry;
using HealthMonitor.Domain.Telemetry;
using Xunit;

namespace HealthMonitor.UnitTests.Telemetry;

/// <summary>
/// Verifies the creation of synthetic telemetry observations
/// from the four supported simulation scenarios.
/// </summary>
/// <remarks>
/// Tests confirm that each scenario produces the fixed
/// measurements specified in Technical Architecture Plan
/// Edition 1.5.
///
/// They also verify observation identity, timestamps,
/// exact scenario matching, timeout measurement semantics,
/// and delegation of domain validation.
///
/// The factory operates entirely in memory. These tests
/// do not access PostgreSQL, create simulation receipts,
/// update telemetry state, or modify incident history.
/// </remarks>
public sealed class TelemetryScenarioFactoryTests
{
    private static readonly DateTimeOffset FixedObservedAtUtc =
        new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Creates a scenario observation using valid defaults.
    /// </summary>
    /// <param name="scenario">
    /// Scenario token passed unchanged to the factory.
    /// </param>
    /// <param name="pingId">
    /// Optional observation identifier. Null generates a GUID;
    /// Guid.Empty is forwarded for domain validation.
    /// </param>
    /// <param name="deviceId">
    /// Optional device identifier. Null generates a GUID;
    /// Guid.Empty is forwarded for domain validation.
    /// </param>
    /// <param name="batchId">
    /// Optional batch identifier. Null generates a GUID;
    /// Guid.Empty is forwarded for domain validation.
    /// </param>
    /// <param name="observedAtUtc">
    /// Optional observation timestamp. Null uses the fixed
    /// UTC instant; invalid supplied timestamps are forwarded.
    /// </param>
    /// <returns>
    /// A telemetry observation generated from the scenario.
    /// </returns>
    private static TelemetryPing Create(
        string scenario = "Healthy",
        Guid? pingId = null,
        Guid? deviceId = null,
        Guid? batchId = null,
        DateTimeOffset? observedAtUtc = null)
    {
        return TelemetryScenarioFactory.Create(
            scenario,
            pingId ?? Guid.NewGuid(),
            deviceId ?? Guid.NewGuid(),
            batchId ?? Guid.NewGuid(),
            observedAtUtc ?? FixedObservedAtUtc);
    }

    [Theory]
    [InlineData("Healthy", 40, 30)]
    [InlineData("HighLatency", 600, 30)]
    [InlineData("HighCpu", 40, 95)]
    public void Create_SuccessScenario_ProducesFixedMeasurements(
        string scenario,
        int expectedLatency,
        int expectedCpu)
    {
        TelemetryPing ping = Create(scenario);

        Assert.Equal(PingOutcome.Success, ping.Outcome);
        Assert.Equal(expectedLatency, ping.LatencyMs);
        Assert.Equal((decimal)expectedCpu, ping.CpuPercent);
        Assert.Equal(FixedObservedAtUtc, ping.ObservedAtUtc);
    }

    [Fact]
    public void Create_TimeoutScenario_HasNullMeasurements()
    {
        TelemetryPing ping = Create("Timeout");

        Assert.Equal(PingOutcome.Timeout, ping.Outcome);
        Assert.Null(ping.LatencyMs);
        Assert.Null(ping.CpuPercent);
        Assert.Equal(FixedObservedAtUtc, ping.ObservedAtUtc);
    }

    [Theory]
    [InlineData("Healthy")]
    [InlineData("HighLatency")]
    [InlineData("HighCpu")]
    [InlineData("Timeout")]
    public void Create_AllScenarios_PreserveSuppliedIdentifiers(
        string scenario)
    {
        Guid pingId = Guid.NewGuid();
        Guid deviceId = Guid.NewGuid();
        Guid batchId = Guid.NewGuid();

        TelemetryPing ping = Create(
            scenario,
            pingId: pingId,
            deviceId: deviceId,
            batchId: batchId);

        Assert.Equal(pingId, ping.Id);
        Assert.Equal(deviceId, ping.DeviceId);
        Assert.Equal(batchId, ping.BatchId);
    }

    [Fact]
    public void Create_SuppliedObservationTimestamp_IsPreserved()
    {
        DateTimeOffset observedAtUtc =
            FixedObservedAtUtc.AddMinutes(17);

        TelemetryPing ping = Create(
            observedAtUtc: observedAtUtc);

        Assert.Equal(observedAtUtc, ping.ObservedAtUtc);
    }

    [Fact]
    public void Create_MultipleDevices_CanShareBatchAndTimestamp()
    {
        Guid batchId = Guid.NewGuid();
        Guid firstDeviceId = Guid.NewGuid();
        Guid secondDeviceId = Guid.NewGuid();

        TelemetryPing first = Create(
            "HighLatency",
            deviceId: firstDeviceId,
            batchId: batchId);

        TelemetryPing second = Create(
            "HighLatency",
            deviceId: secondDeviceId,
            batchId: batchId);

        // Every selected device gets a separate observation,
        // but the batch identity and server-captured observation
        // time are shared across the transaction.
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.DeviceId, second.DeviceId);

        Assert.Equal(batchId, first.BatchId);
        Assert.Equal(batchId, second.BatchId);

        Assert.Equal(first.ObservedAtUtc, second.ObservedAtUtc);
        Assert.Equal(first.LatencyMs, second.LatencyMs);
        Assert.Equal(first.CpuPercent, second.CpuPercent);
    }

    [Fact]
    public void Create_RepeatedScenario_IsDeterministicInMeasurements()
    {
        TelemetryPing first = Create("HighCpu");
        TelemetryPing second = Create("HighCpu");

        // Scenario selection, not entity identity, determines
        // the synthetic measurements.
        Assert.Equal(first.Outcome, second.Outcome);
        Assert.Equal(first.LatencyMs, second.LatencyMs);
        Assert.Equal(first.CpuPercent, second.CpuPercent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("healthy")]
    [InlineData("HEALTHY")]
    [InlineData("Healthy ")]
    [InlineData(" Healthy")]
    [InlineData("HighCPU")]
    [InlineData("Unknown")]
    public void Create_UnsupportedScenario_ThrowsArgumentException(
        string scenario)
    {
        Assert.Throws<ArgumentException>(
            () => Create(scenario));
    }

    [Fact]
    public void Create_NullScenario_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => Create(null!));
    }

    [Theory]
    [InlineData("Healthy")]
    [InlineData("HighLatency")]
    [InlineData("HighCpu")]
    [InlineData("Timeout")]
    public void Create_EmptyPingId_IsRejectedByDomain(
        string scenario)
    {
        Assert.Throws<ArgumentException>(
            () => Create(
                scenario,
                pingId: Guid.Empty));
    }

    [Theory]
    [InlineData("Healthy")]
    [InlineData("HighLatency")]
    [InlineData("HighCpu")]
    [InlineData("Timeout")]
    public void Create_EmptyDeviceId_IsRejectedByDomain(
        string scenario)
    {
        Assert.Throws<ArgumentException>(
            () => Create(
                scenario,
                deviceId: Guid.Empty));
    }

    [Theory]
    [InlineData("Healthy")]
    [InlineData("HighLatency")]
    [InlineData("HighCpu")]
    [InlineData("Timeout")]
    public void Create_EmptyBatchId_IsRejectedByDomain(
        string scenario)
    {
        Assert.Throws<ArgumentException>(
            () => Create(
                scenario,
                batchId: Guid.Empty));
    }

    [Theory]
    [InlineData("Healthy")]
    [InlineData("HighLatency")]
    [InlineData("HighCpu")]
    [InlineData("Timeout")]
    public void Create_NonUtcTimestamp_IsRejectedByDomain(
        string scenario)
    {
        DateTimeOffset nonUtcTimestamp =
            new(2026, 10, 15, 12, 0, 0, TimeSpan.FromHours(-6));

        Assert.Throws<ArgumentException>(
            () => Create(
                scenario,
                observedAtUtc: nonUtcTimestamp));
    }

    [Theory]
    [InlineData("Healthy")]
    [InlineData("HighLatency")]
    [InlineData("HighCpu")]
    [InlineData("Timeout")]
    public void Create_DefaultTimestamp_IsRejectedByDomain(
        string scenario)
    {
        // The domain must reject a missing or default observation
        // timestamp rather than persisting an invalid date.
        //
        // This test calls the factory directly because the test
        // helper uses null to select a valid timestamp.
        Assert.Throws<ArgumentException>(
            () => TelemetryScenarioFactory.Create(
                scenario,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                default));
    }
}
