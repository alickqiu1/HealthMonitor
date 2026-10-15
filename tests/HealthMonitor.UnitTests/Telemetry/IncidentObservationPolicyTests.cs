
using System;
using HealthMonitor.Domain.Telemetry;
using Xunit;

namespace HealthMonitor.UnitTests.Telemetry;

/// <summary>
/// Verifies the incident actions determined by the
/// IncidentObservationPolicy domain service.
/// </summary>
/// <remarks>
/// Tests cover latency, CPU utilization, and connectivity
/// incident decisions for successful and timeout observations.
///
/// Boundary tests verify that latency greater than 250 milliseconds
/// and CPU utilization greater than 80 percent trigger incidents.
/// Values exactly at the thresholds are not violations.
///
/// Tests also verify that timeout observations do not resolve
/// existing latency or CPU incidents.
///
/// These are isolated domain unit tests. They do not execute
/// database operations or modify incident entities.
/// </remarks>
public sealed class IncidentObservationPolicyTests
{
    // Fixed timestamps keep test results independent of wall-clock time.
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Creates a successful observation with valid default values.
    /// </summary>
    /// <param name="latencyMs">Observed latency in milliseconds.</param>
    /// <param name="cpuPercent">Observed CPU utilization percentage.</param>
    /// <returns>A validated successful telemetry observation.</returns>
    /// <remarks>
    /// Each call generates independent ping, device, and batch IDs.
    /// Validation remains the responsibility of TelemetryPing.
    /// </remarks>
    private static TelemetryPing CreateSuccess(
        int latencyMs = 100,
        decimal cpuPercent = 25m)
    {
        return TelemetryPing.CreateSuccess(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            ObservedAt,
            latencyMs,
            cpuPercent);
    }

    /// <summary>
    /// Creates a timeout observation without measurements.
    /// </summary>
    /// <returns>A validated timeout observation.</returns>
    private static TelemetryPing CreateTimeout()
    {
        return TelemetryPing.CreateTimeout(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            ObservedAt);
    }

    [Theory]
    [InlineData(0, IncidentObservationAction.Resolve)]
    [InlineData(100, IncidentObservationAction.Resolve)]
    [InlineData(249, IncidentObservationAction.Resolve)]
    [InlineData(250, IncidentObservationAction.Resolve)]
    [InlineData(251, IncidentObservationAction.EnsureOpen)]
    [InlineData(60000, IncidentObservationAction.EnsureOpen)]
    public void Evaluate_Latency_UsesStrictThreshold(
        int latencyMs,
        IncidentObservationAction expected)
    {
        TelemetryPing ping = CreateSuccess(latencyMs: latencyMs);

        IncidentObservationAction action =
            IncidentObservationPolicy.Evaluate(
                IncidentKind.Latency,
                ping);

        Assert.Equal(expected, action);
    }

    [Theory]
    [InlineData("0", IncidentObservationAction.Resolve)]
    [InlineData("25.50", IncidentObservationAction.Resolve)]
    [InlineData("79.99", IncidentObservationAction.Resolve)]
    [InlineData("80", IncidentObservationAction.Resolve)]
    [InlineData("80.01", IncidentObservationAction.EnsureOpen)]
    [InlineData("100", IncidentObservationAction.EnsureOpen)]
    public void Evaluate_Cpu_UsesStrictThreshold(
        string cpuText,
        IncidentObservationAction expected)
    {
        decimal cpuPercent = decimal.Parse(
            cpuText,
            System.Globalization.CultureInfo.InvariantCulture);

        TelemetryPing ping = CreateSuccess(
            cpuPercent: cpuPercent);

        IncidentObservationAction action =
            IncidentObservationPolicy.Evaluate(
                IncidentKind.Cpu,
                ping);

        Assert.Equal(expected, action);
    }

    [Fact]
    public void Evaluate_Timeout_EnsuresConnectivityIncidentOpen()
    {
        TelemetryPing ping = CreateTimeout();

        IncidentObservationAction action =
            IncidentObservationPolicy.Evaluate(
                IncidentKind.Connectivity,
                ping);

        Assert.Equal(IncidentObservationAction.EnsureOpen, action);
    }

    [Fact]
    public void Evaluate_Success_ResolvesConnectivityIncident()
    {
        TelemetryPing ping = CreateSuccess();

        IncidentObservationAction action =
            IncidentObservationPolicy.Evaluate(
                IncidentKind.Connectivity,
                ping);

        Assert.Equal(IncidentObservationAction.Resolve, action);
    }

    [Theory]
    [InlineData(IncidentKind.Latency)]
    [InlineData(IncidentKind.Cpu)]
    public void Evaluate_Timeout_DoesNotResolveMeasurementIncidents(
        IncidentKind kind)
    {
        TelemetryPing ping = CreateTimeout();

        IncidentObservationAction action =
            IncidentObservationPolicy.Evaluate(kind, ping);

        Assert.Equal(IncidentObservationAction.Keep, action);
    }

    [Fact]
    public void Evaluate_HighLatencyNormalCpu_OnlyOpensLatency()
    {
        TelemetryPing ping = CreateSuccess(
            latencyMs: 251,
            cpuPercent: 40m);

        Assert.Equal(
            IncidentObservationAction.EnsureOpen,
            IncidentObservationPolicy.Evaluate(
                IncidentKind.Latency,
                ping));

        Assert.Equal(
            IncidentObservationAction.Resolve,
            IncidentObservationPolicy.Evaluate(
                IncidentKind.Cpu,
                ping));

        Assert.Equal(
            IncidentObservationAction.Resolve,
            IncidentObservationPolicy.Evaluate(
                IncidentKind.Connectivity,
                ping));
    }

    [Fact]
    public void Evaluate_NormalLatencyHighCpu_OnlyOpensCpu()
    {
        TelemetryPing ping = CreateSuccess(
            latencyMs: 100,
            cpuPercent: 80.01m);

        Assert.Equal(
            IncidentObservationAction.Resolve,
            IncidentObservationPolicy.Evaluate(
                IncidentKind.Latency,
                ping));

        Assert.Equal(
            IncidentObservationAction.EnsureOpen,
            IncidentObservationPolicy.Evaluate(
                IncidentKind.Cpu,
                ping));

        Assert.Equal(
            IncidentObservationAction.Resolve,
            IncidentObservationPolicy.Evaluate(
                IncidentKind.Connectivity,
                ping));
    }

    [Fact]
    public void Evaluate_HighLatencyAndHighCpu_OpensBoth()
    {
        TelemetryPing ping = CreateSuccess(
            latencyMs: 500,
            cpuPercent: 95m);

        Assert.Equal(
            IncidentObservationAction.EnsureOpen,
            IncidentObservationPolicy.Evaluate(
                IncidentKind.Latency,
                ping));

        Assert.Equal(
            IncidentObservationAction.EnsureOpen,
            IncidentObservationPolicy.Evaluate(
                IncidentKind.Cpu,
                ping));

        Assert.Equal(
            IncidentObservationAction.Resolve,
            IncidentObservationPolicy.Evaluate(
                IncidentKind.Connectivity,
                ping));
    }

    [Fact]
    public void Evaluate_HealthyObservation_ResolvesAllIncidentKinds()
    {
        TelemetryPing ping = CreateSuccess(
            latencyMs: 250,
            cpuPercent: 80m);

        foreach (IncidentKind kind in Enum.GetValues<IncidentKind>())
        {
            Assert.Equal(
                IncidentObservationAction.Resolve,
                IncidentObservationPolicy.Evaluate(kind, ping));
        }
    }

    [Fact]
    public void Evaluate_NullPing_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => IncidentObservationPolicy.Evaluate(
                IncidentKind.Latency,
                null!));
    }

    [Theory]
    [InlineData((IncidentKind)(-1))]
    [InlineData((IncidentKind)999)]
    public void Evaluate_UndefinedIncidentKind_Throws(
        IncidentKind kind)
    {
        TelemetryPing ping = CreateSuccess();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => IncidentObservationPolicy.Evaluate(kind, ping));
    }

    [Fact]
    public void Evaluate_SameObservation_IsDeterministic()
    {
        TelemetryPing ping = CreateSuccess(
            latencyMs: 275,
            cpuPercent: 90m);

        IncidentObservationAction first =
            IncidentObservationPolicy.Evaluate(
                IncidentKind.Latency,
                ping);

        IncidentObservationAction second =
            IncidentObservationPolicy.Evaluate(
                IncidentKind.Latency,
                ping);

        Assert.Equal(first, second);
        Assert.Equal(IncidentObservationAction.EnsureOpen, first);
    }
}
