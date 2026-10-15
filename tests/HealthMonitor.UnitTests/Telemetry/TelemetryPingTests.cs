
using System;
using System.Globalization;
using HealthMonitor.Domain.Telemetry;
using Xunit;

namespace HealthMonitor.UnitTests.Telemetry;

/// <summary>
/// Verifies the domain invariants and creation contracts
/// of the TelemetryPing entity.
/// </summary>
/// <remarks>
/// Tests cover successful and timeout observations, identifier
/// validation, UTC timestamps, latency boundaries, CPU utilization
/// boundaries, and CPU measurement precision.
///
/// Both factory methods are tested because they represent different
/// valid observation states:
/// - Success requires latency and CPU measurements.
/// - Timeout requires both measurements to be absent.
///
/// These tests exercise domain behavior independently of EF Core,
/// PostgreSQL, and external services.
/// </remarks>
public sealed class TelemetryPingTests
{
    // Use a fixed UTC timestamp so validation and boundary tests
    // are deterministic and independent of the system clock.
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Creates a successful telemetry observation using valid
    /// defaults, allowing individual tests to override inputs.
    /// </summary>
    /// <param name="id">
    /// Optional observation identifier. Null generates a new GUID;
    /// Guid.Empty is passed through for domain validation.
    /// </param>
    /// <param name="deviceId">
    /// Optional device identifier. Null generates a new GUID;
    /// Guid.Empty is passed through for domain validation.
    /// </param>
    /// <param name="batchId">
    /// Optional simulation batch identifier. Null generates a new GUID;
    /// Guid.Empty is passed through for domain validation.
    /// </param>
    /// <param name="observedAtUtc">
    /// Optional observation timestamp, defaulting to the fixed test time.
    /// </param>
    /// <param name="latencyMs">Synthetic latency in milliseconds.</param>
    /// <param name="cpuPercent">Synthetic CPU utilization percentage.</param>
    /// <returns>A validated successful telemetry observation.</returns>
    /// <remarks>
    /// Nullable GUID parameters distinguish omitted identifiers from
    /// explicitly invalid Guid.Empty values. The latter are forwarded
    /// unchanged to verify the entity's validation rules.
    ///
    /// Normalization and validation remain the responsibility of
    /// TelemetryPing.CreateSuccess.
    ///
    /// To test an explicitly supplied default DateTimeOffset,
    /// invoke the domain factory directly because null selects
    /// the helper's default timestamp.
    /// </remarks>
    private static TelemetryPing CreateSuccess(
        Guid? id = null,
        Guid? deviceId = null,
        Guid? batchId = null,
        DateTimeOffset? observedAtUtc = null,
        int latencyMs = 100,
        decimal cpuPercent = 25.50m)
    {
        return TelemetryPing.CreateSuccess(
            id ?? Guid.NewGuid(),
            deviceId ?? Guid.NewGuid(),
            batchId ?? Guid.NewGuid(),
            observedAtUtc ?? ObservedAt,
            latencyMs,
            cpuPercent);
    }

    /// <summary>
    /// Creates a timeout observation using valid default values.
    /// </summary>
    /// <param name="id">
    /// Optional observation identifier. Null generates a new GUID;
    /// Guid.Empty is forwarded for validation.
    /// </param>
    /// <param name="deviceId">
    /// Optional device identifier. Null generates a new GUID;
    /// Guid.Empty is forwarded for validation.
    /// </param>
    /// <param name="batchId">
    /// Optional simulation batch identifier. Null generates a new GUID;
    /// Guid.Empty is forwarded for validation.
    /// </param>
    /// <param name="observedAtUtc">
    /// Optional observation timestamp, defaulting to the fixed test time.
    /// </param>
    /// <returns>A validated timeout observation.</returns>
    /// <remarks>
    /// Nullable GUID parameters allow tests to distinguish omitted
    /// identifiers from explicitly invalid Guid.Empty identifiers.
    ///
    /// The timeout factory does not accept measurement parameters,
    /// preventing callers from attaching latency or CPU values
    /// to timeout observations.
    /// </remarks>
    private static TelemetryPing CreateTimeout(
        Guid? id = null,
        Guid? deviceId = null,
        Guid? batchId = null,
        DateTimeOffset? observedAtUtc = null)
    {
        return TelemetryPing.CreateTimeout(
            id ?? Guid.NewGuid(),
            deviceId ?? Guid.NewGuid(),
            batchId ?? Guid.NewGuid(),
            observedAtUtc ?? ObservedAt);
    }

    [Fact]
    public void CreateSuccess_ValidValues_CreatesSuccessfulObservation()
    {
        Guid id = Guid.NewGuid();
        Guid deviceId = Guid.NewGuid();
        Guid batchId = Guid.NewGuid();

        TelemetryPing ping = CreateSuccess(
            id: id,
            deviceId: deviceId,
            batchId: batchId,
            latencyMs: 250,
            cpuPercent: 80.12m);

        Assert.Equal(id, ping.Id);
        Assert.Equal(deviceId, ping.DeviceId);
        Assert.Equal(batchId, ping.BatchId);
        Assert.Equal(ObservedAt, ping.ObservedAtUtc);
        Assert.Equal(PingOutcome.Success, ping.Outcome);
        Assert.Equal(250, ping.LatencyMs);
        Assert.Equal(80.12m, ping.CpuPercent);
    }

    [Fact]
    public void CreateTimeout_ValidValues_CreatesObservationWithoutMeasurements()
    {
        Guid id = Guid.NewGuid();
        Guid deviceId = Guid.NewGuid();
        Guid batchId = Guid.NewGuid();

        TelemetryPing ping = CreateTimeout(
            id: id,
            deviceId: deviceId,
            batchId: batchId);

        Assert.Equal(id, ping.Id);
        Assert.Equal(deviceId, ping.DeviceId);
        Assert.Equal(batchId, ping.BatchId);
        Assert.Equal(ObservedAt, ping.ObservedAtUtc);
        Assert.Equal(PingOutcome.Timeout, ping.Outcome);
        Assert.Null(ping.LatencyMs);
        Assert.Null(ping.CpuPercent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_InvalidPingId_Throws(bool timeout)
    {
        Assert.Throws<ArgumentException>(() =>
        {
            if (timeout)
                CreateTimeout(id: Guid.Empty);
            else
                CreateSuccess(id: Guid.Empty);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_InvalidDeviceId_Throws(bool timeout)
    {
        Assert.Throws<ArgumentException>(() =>
        {
            if (timeout)
                CreateTimeout(deviceId: Guid.Empty);
            else
                CreateSuccess(deviceId: Guid.Empty);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_InvalidBatchId_Throws(bool timeout)
    {
        Assert.Throws<ArgumentException>(() =>
        {
            if (timeout)
                CreateTimeout(batchId: Guid.Empty);
            else
                CreateSuccess(batchId: Guid.Empty);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_DefaultTimestamp_Throws(bool timeout)
    {
        // Pass the default timestamp directly; helper defaults
        // would otherwise replace null with the fixed valid time.
        if (timeout)
        {
            Assert.Throws<ArgumentException>(() =>
                TelemetryPing.CreateTimeout(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    default(DateTimeOffset)));
        }
        else
        {
            Assert.Throws<ArgumentException>(() =>
                TelemetryPing.CreateSuccess(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    default(DateTimeOffset),
                    100,
                    25.50m));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_NonUtcTimestamp_Throws(bool timeout)
    {
        DateTimeOffset nonUtc =
            new(2026, 10, 14, 12, 0, 0, TimeSpan.FromHours(-6));

        if (timeout)
        {
            Assert.Throws<ArgumentException>(
                () => CreateTimeout(observedAtUtc: nonUtc));
        }
        else
        {
            Assert.Throws<ArgumentException>(
                () => CreateSuccess(observedAtUtc: nonUtc));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(60000)]
    public void CreateSuccess_ValidLatency_AcceptsValue(int latencyMs)
    {
        TelemetryPing ping = CreateSuccess(latencyMs: latencyMs);

        Assert.Equal(PingOutcome.Success, ping.Outcome);
        Assert.Equal(latencyMs, ping.LatencyMs);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60001)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void CreateSuccess_InvalidLatency_Throws(int latencyMs)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateSuccess(latencyMs: latencyMs));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.01")]
    [InlineData("25.50")]
    [InlineData("80.12")]
    [InlineData("99.99")]
    [InlineData("100")]
    public void CreateSuccess_ValidCpu_AcceptsValue(string cpuText)
    {
        decimal cpuPercent = decimal.Parse(
            cpuText,
            CultureInfo.InvariantCulture);

        TelemetryPing ping = CreateSuccess(
            cpuPercent: cpuPercent);

        Assert.Equal(PingOutcome.Success, ping.Outcome);
        Assert.Equal(cpuPercent, ping.CpuPercent);
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("-1")]
    [InlineData("100.01")]
    [InlineData("101")]
    public void CreateSuccess_CpuOutsideRange_Throws(string cpuText)
    {
        decimal cpuPercent = decimal.Parse(
            cpuText,
            CultureInfo.InvariantCulture);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateSuccess(cpuPercent: cpuPercent));
    }

    [Theory]
    [InlineData("0.001")]
    [InlineData("80.123")]
    [InlineData("99.999")]
    public void CreateSuccess_CpuExcessPrecision_Throws(string cpuText)
    {
        decimal cpuPercent = decimal.Parse(
            cpuText,
            CultureInfo.InvariantCulture);

        Assert.Throws<ArgumentException>(
            () => CreateSuccess(cpuPercent: cpuPercent));
    }

    [Fact]
    public void CreateSuccess_CpuWithTrailingZeroes_IsAccepted()
    {
        // The domain compares decimal numeric values after rounding,
        // rather than rejecting values based on their decimal scale.
        decimal cpuPercent = 80.120m;

        TelemetryPing ping = CreateSuccess(
            cpuPercent: cpuPercent);

        Assert.Equal(cpuPercent, ping.CpuPercent);
    }

    [Fact]
    public void CreateSuccess_ZeroLatencyAndCpu_IsAccepted()
    {
        TelemetryPing ping = CreateSuccess(
            latencyMs: 0,
            cpuPercent: 0m);

        Assert.Equal(0, ping.LatencyMs);
        Assert.Equal(0m, ping.CpuPercent);
    }

    [Fact]
    public void CreateSuccess_MaximumLatencyAndCpu_IsAccepted()
    {
        TelemetryPing ping = CreateSuccess(
            latencyMs: 60000,
            cpuPercent: 100m);

        Assert.Equal(60000, ping.LatencyMs);
        Assert.Equal(100m, ping.CpuPercent);
    }

    [Fact]
    public void CreateSuccess_UsesSuppliedObservationTimestamp()
    {
        DateTimeOffset timestamp = ObservedAt.AddMinutes(15);

        TelemetryPing ping = CreateSuccess(
            observedAtUtc: timestamp);

        Assert.Equal(timestamp, ping.ObservedAtUtc);
    }

    [Fact]
    public void CreateTimeout_UsesSuppliedObservationTimestamp()
    {
        DateTimeOffset timestamp = ObservedAt.AddMinutes(15);

        TelemetryPing ping = CreateTimeout(
            observedAtUtc: timestamp);

        Assert.Equal(timestamp, ping.ObservedAtUtc);
    }

    [Fact]
    public void CreateSuccess_SeparateCalls_CreateIndependentObservations()
    {
        TelemetryPing first = CreateSuccess();
        TelemetryPing second = CreateSuccess();

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void CreateTimeout_SeparateCalls_CreateIndependentObservations()
    {
        TelemetryPing first = CreateTimeout();
        TelemetryPing second = CreateTimeout();

        Assert.NotEqual(first.Id, second.Id);
    }
}
