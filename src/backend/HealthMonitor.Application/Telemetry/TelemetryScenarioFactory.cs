
using System;
using HealthMonitor.Domain.Telemetry;

namespace HealthMonitor.Application.Telemetry;

/// <summary>
/// Creates validated synthetic telemetry observations from
/// the four supported simulation scenarios.
/// </summary>
/// <remarks>
/// Implements the fixed scenario measurements specified in
/// HealthMonitor Technical Architecture Plan Edition 1.5.
///
/// Supported scenarios:
///
/// Healthy:
///   Success, 40 ms latency, 30 percent CPU.
///
/// HighLatency:
///   Success, 600 ms latency, 30 percent CPU.
///
/// HighCpu:
///   Success, 40 ms latency, 95 percent CPU.
///
/// Timeout:
///   Timeout, no latency or CPU measurements.
///
/// The factory does not perform real network or hardware
/// monitoring. All observations are explicitly synthetic.
///
/// Measurement values are defined centrally so callers cannot
/// accidentally generate inconsistent results for the same
/// simulation scenario.
///
/// The factory has no database or system-clock dependencies.
/// It does not save the generated observation or update
/// device telemetry state or incident history.
/// </remarks>
public static class TelemetryScenarioFactory
{
    private const string HealthyScenario = "Healthy";
    private const string HighLatencyScenario = "HighLatency";
    private const string HighCpuScenario = "HighCpu";
    private const string TimeoutScenario = "Timeout";

    private const int HealthyLatencyMs = 40;
    private const int HighLatencyMs = 600;

    private const decimal NormalCpuPercent = 30m;
    private const decimal HighCpuPercent = 95m;

    /// <summary>
    /// Creates one synthetic telemetry observation using
    /// the measurements assigned to the requested scenario.
    /// </summary>
    /// <param name="scenario">
    /// Canonical scenario identifier: Healthy, HighLatency,
    /// HighCpu, or Timeout.
    /// </param>
    /// <param name="pingId">
    /// Unique identifier assigned to the new observation.
    /// </param>
    /// <param name="deviceId">
    /// Identifier of the monitored device being observed.
    /// </param>
    /// <param name="batchId">
    /// Identifier of the simulation batch that owns the observation.
    /// </param>
    /// <param name="observedAtUtc">
    /// UTC observation timestamp captured by the ingestion service
    /// after acquiring the database advisory lock.
    /// </param>
    /// <returns>
    /// A validated TelemetryPing containing the fixed measurements
    /// associated with the requested simulation scenario.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when the scenario is unsupported or a required
    /// identifier or timestamp violates TelemetryPing validation.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the observation timestamp violates the
    /// TelemetryPing domain factory's UTC requirements.
    /// </exception>
    /// <remarks>
    /// Scenario names must match their canonical tokens exactly.
    /// The factory does not trim strings or ignore case.
    ///
    /// The ingestion service must validate the complete request,
    /// UUIDv7 freshness, and device catalog eligibility before
    /// invoking this method for a new batch.
    ///
    /// The caller supplies the observation timestamp rather
    /// than allowing the factory to read TimeProvider or
    /// DateTimeOffset.UtcNow.
    ///
    /// This ensures that all observations in an ingestion batch
    /// can share one consistent server-captured timestamp.
    ///
    /// UUID identity validation, including nonempty ping,
    /// device, and batch identifiers, is delegated to the
    /// TelemetryPing domain factories.
    /// </remarks>
    public static TelemetryPing Create(
        string scenario,
        Guid pingId,
        Guid deviceId,
        Guid batchId,
        DateTimeOffset observedAtUtc)
    {
        // Use exact scenario tokens instead of silently normalizing.
        //
        // The scenario is part of the canonical request hash
        // persisted in the simulation receipt. Interpreting a
        // noncanonical spelling as a valid scenario could make
        // replay behavior inconsistent with request validation.
        return scenario switch
        {
            HealthyScenario => CreateSuccess(
                pingId,
                deviceId,
                batchId,
                observedAtUtc,
                HealthyLatencyMs,
                NormalCpuPercent),

            HighLatencyScenario => CreateSuccess(
                pingId,
                deviceId,
                batchId,
                observedAtUtc,
                HighLatencyMs,
                NormalCpuPercent),

            HighCpuScenario => CreateSuccess(
                pingId,
                deviceId,
                batchId,
                observedAtUtc,
                HealthyLatencyMs,
                HighCpuPercent),

            TimeoutScenario => CreateTimeout(
                pingId,
                deviceId,
                batchId,
                observedAtUtc),

            _ => throw new ArgumentException(
                "Unsupported simulation scenario. " +
                "Expected Healthy, HighLatency, HighCpu, or Timeout.",
                nameof(scenario))
        };
    }

    /// <summary>
    /// Creates a successful observation using the supplied
    /// fixed scenario measurements.
    /// </summary>
    /// <param name="pingId">Observation identifier.</param>
    /// <param name="deviceId">Monitored device identifier.</param>
    /// <param name="batchId">Owning simulation batch identifier.</param>
    /// <param name="observedAtUtc">Server-captured UTC timestamp.</param>
    /// <param name="latencyMs">Scenario latency in milliseconds.</param>
    /// <param name="cpuPercent">Scenario CPU utilization percentage.</param>
    /// <returns>A validated successful telemetry observation.</returns>
    private static TelemetryPing CreateSuccess(
        Guid pingId,
        Guid deviceId,
        Guid batchId,
        DateTimeOffset observedAtUtc,
        int latencyMs,
        decimal cpuPercent)
    {
        // Delegate entity construction and measurement validation
        // to the domain factory instead of duplicating its
        // invariants in the Application layer.
        //
        // Success observations must contain both measurements.
        return TelemetryPing.CreateSuccess(
            pingId,
            deviceId,
            batchId,
            observedAtUtc,
            latencyMs,
            cpuPercent);
    }

    /// <summary>
    /// Creates a timeout observation without synthetic
    /// latency or CPU measurements.
    /// </summary>
    /// <param name="pingId">Observation identifier.</param>
    /// <param name="deviceId">Monitored device identifier.</param>
    /// <param name="batchId">Owning simulation batch identifier.</param>
    /// <param name="observedAtUtc">Server-captured UTC timestamp.</param>
    /// <returns>A validated timeout telemetry observation.</returns>
    private static TelemetryPing CreateTimeout(
        Guid pingId,
        Guid deviceId,
        Guid batchId,
        DateTimeOffset observedAtUtc)
    {
        // A timeout supplies no usable latency or CPU data.
        //
        // Assigning zero measurements would incorrectly
        // suggest healthy readings and could cause existing
        // measurement-related incidents to be resolved.
        //
        // The domain timeout factory preserves null measurements.
        return TelemetryPing.CreateTimeout(
            pingId,
            deviceId,
            batchId,
            observedAtUtc);
    }
}
