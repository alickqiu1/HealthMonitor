
using System;

namespace HealthMonitor.Domain.Telemetry;

/// <summary>
/// Represents one synthetic health observation for a monitored device.
/// </summary>
/// <remarks>
/// This entity belongs to the Telemetry bounded context and records
/// an individual successful observation or timeout.
///
/// A successful observation requires latency and CPU measurements.
/// A timeout contains neither measurement.
///
/// Observations use application-generated identifiers and UTC
/// timestamps. Each observation references a simulation batch and
/// a device by their scalar identifiers, without cross-module
/// navigation properties.
///
/// A telemetry ping represents a simulated health observation,
/// not a real ICMP or industrial protocol request.
///
/// Health classification and incident lifecycle decisions are
/// performed separately using telemetry history and policy rules.
/// </remarks>
public sealed class TelemetryPing
{
    /// <summary>
    /// Gets the unique identifier of this observation.
    /// </summary>
    public Guid Id { get; private set; }

    /// <summary>
    /// Gets the identifier of the observed device.
    /// </summary>
    public Guid DeviceId { get; private set; }

    /// <summary>
    /// Gets the simulation batch that produced this observation.
    /// </summary>
    public Guid BatchId { get; private set; }

    /// <summary>
    /// Gets the UTC timestamp when the observation occurred.
    /// </summary>
    public DateTimeOffset ObservedAtUtc { get; private set; }

    /// <summary>
    /// Gets whether this observation succeeded or timed out.
    /// </summary>
    public PingOutcome Outcome { get; private set; }

    /// <summary>
    /// Gets the synthetic response latency in milliseconds.
    /// </summary>
    /// <remarks>
    /// The valid range is 0 to 60,000 milliseconds, inclusive.
    /// Null indicates a timeout observation.
    /// </remarks>
    public int? LatencyMs { get; private set; }

    /// <summary>
    /// Gets the synthetic CPU utilization percentage.
    /// </summary>
    /// <remarks>
    /// The valid range is 0 to 100 percent, inclusive,
    /// with at most two decimal places.
    ///
    /// Null indicates a timeout observation.
    /// </remarks>
    public decimal? CpuPercent { get; private set; }

    // Required by EF Core for entity materialization.
    // Application code creates observations through the factory methods.
    private TelemetryPing() { }

    /// <summary>
    /// Creates and validates a telemetry observation.
    /// </summary>
    /// <param name="id">Non-empty observation identifier.</param>
    /// <param name="deviceId">Non-empty device identifier.</param>
    /// <param name="batchId">Non-empty simulation batch identifier.</param>
    /// <param name="observedAtUtc">
    /// Non-default observation timestamp with a zero UTC offset.
    /// </param>
    /// <param name="outcome">Defined observation outcome.</param>
    /// <param name="latencyMs">
    /// Latency for a successful observation, otherwise null.
    /// </param>
    /// <param name="cpuPercent">
    /// CPU percentage for a successful observation, otherwise null.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when an identifier or timestamp is invalid, required
    /// measurements are missing, timeout measurements are provided,
    /// or CPU precision exceeds two decimal places.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the outcome is undefined or a measurement
    /// falls outside its permitted range.
    /// </exception>
    /// <remarks>
    /// All values are validated before assigning entity properties.
    /// The private constructor ensures callers use the explicit
    /// success and timeout factory methods.
    /// </remarks>
    private TelemetryPing(
        Guid id,
        Guid deviceId,
        Guid batchId,
        DateTimeOffset observedAtUtc,
        PingOutcome outcome,
        int? latencyMs,
        decimal? cpuPercent)
    {
        if (id == Guid.Empty)
            throw new ArgumentException(
                "Ping ID cannot be empty.", nameof(id));

        if (deviceId == Guid.Empty)
            throw new ArgumentException(
                "Device ID cannot be empty.", nameof(deviceId));

        if (batchId == Guid.Empty)
            throw new ArgumentException(
                "Batch ID cannot be empty.", nameof(batchId));

        if (observedAtUtc == default ||
            observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "A valid UTC observation timestamp is required.",
                nameof(observedAtUtc));
        }

        if (!Enum.IsDefined(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome));

        if (outcome == PingOutcome.Timeout)
        {
            if (latencyMs.HasValue || cpuPercent.HasValue)
            {
                throw new ArgumentException(
                    "Timeout observations cannot contain measurements.");
            }
        }
        else
        {
            if (!latencyMs.HasValue || !cpuPercent.HasValue)
            {
                throw new ArgumentException(
                    "Successful observations require both measurements.");
            }

            if (latencyMs.Value < 0 || latencyMs.Value > 60000)
                throw new ArgumentOutOfRangeException(nameof(latencyMs));

            if (cpuPercent.Value < 0m || cpuPercent.Value > 100m)
                throw new ArgumentOutOfRangeException(nameof(cpuPercent));

            if (decimal.Round(cpuPercent.Value, 2) != cpuPercent.Value)
            {
                throw new ArgumentException(
                    "CPU percentage must have at most two decimal places.",
                    nameof(cpuPercent));
            }
        }

        Id = id;
        DeviceId = deviceId;
        BatchId = batchId;
        ObservedAtUtc = observedAtUtc;
        Outcome = outcome;
        LatencyMs = latencyMs;
        CpuPercent = cpuPercent;
    }

    /// <summary>
    /// Creates a successful synthetic health observation.
    /// </summary>
    /// <param name="id">Non-empty observation identifier.</param>
    /// <param name="deviceId">Non-empty device identifier.</param>
    /// <param name="batchId">Non-empty simulation batch identifier.</param>
    /// <param name="observedAtUtc">
    /// Non-default observation timestamp with a zero UTC offset.
    /// </param>
    /// <param name="latencyMs">
    /// Response latency from 0 to 60,000 milliseconds.
    /// </param>
    /// <param name="cpuPercent">
    /// CPU utilization from 0 to 100 percent, with at most
    /// two decimal places.
    /// </param>
    /// <returns>A validated successful telemetry observation.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when identifiers, timestamp, or CPU precision are invalid.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when a measurement is outside its permitted range.
    /// </exception>
    public static TelemetryPing CreateSuccess(
        Guid id,
        Guid deviceId,
        Guid batchId,
        DateTimeOffset observedAtUtc,
        int latencyMs,
        decimal cpuPercent)
    {
        return new TelemetryPing(
            id,
            deviceId,
            batchId,
            observedAtUtc,
            PingOutcome.Success,
            latencyMs,
            cpuPercent);
    }

    /// <summary>
    /// Creates a synthetic timeout observation without measurements.
    /// </summary>
    /// <param name="id">Non-empty observation identifier.</param>
    /// <param name="deviceId">Non-empty device identifier.</param>
    /// <param name="batchId">Non-empty simulation batch identifier.</param>
    /// <param name="observedAtUtc">
    /// Non-default observation timestamp with a zero UTC offset.
    /// </param>
    /// <returns>A validated timeout observation.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when an identifier or observation timestamp is invalid.
    /// </exception>
    /// <remarks>
    /// Timeout observations have null latency and CPU measurements.
    /// Health classification and connectivity incident handling
    /// occur outside this entity.
    /// </remarks>
    public static TelemetryPing CreateTimeout(
        Guid id,
        Guid deviceId,
        Guid batchId,
        DateTimeOffset observedAtUtc)
    {
        return new TelemetryPing(
            id,
            deviceId,
            batchId,
            observedAtUtc,
            PingOutcome.Timeout,
            null,
            null);
    }
}
