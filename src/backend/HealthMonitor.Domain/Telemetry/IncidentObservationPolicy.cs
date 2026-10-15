
using System;

namespace HealthMonitor.Domain.Telemetry;

/// <summary>
/// Defines the lifecycle action indicated by a telemetry observation
/// for a particular incident category.
/// </summary>
/// <remarks>
/// The action describes what the incident lifecycle processor
/// should do; it does not indicate whether an incident exists.
///
/// Keep means the observation provides no reason to change
/// the current incident state.
///
/// EnsureOpen means an incident should exist. If one is already
/// open for the device and category, it must be preserved.
///
/// Resolve means an existing incident should be resolved.
/// If no incident is open, no action is necessary.
/// </remarks>
public enum IncidentObservationAction
{
    /// <summary>
    /// Preserve the existing incident state without opening
    /// or resolving an incident.
    /// </summary>
    Keep,

    /// <summary>
    /// Ensure that an incident is open, preserving an existing
    /// open incident rather than creating a duplicate.
    /// </summary>
    EnsureOpen,

    /// <summary>
    /// Resolve an existing open incident, if one exists.
    /// </summary>
    Resolve
}

/// <summary>
/// Determines incident lifecycle actions from validated
/// synthetic telemetry observations.
/// </summary>
/// <remarks>
/// Implements the incident rules defined in HealthMonitor
/// Technical Architecture Plan Edition 1.5.
///
/// Latency and CPU incidents are evaluated independently,
/// allowing both conditions to exist simultaneously.
///
/// A successful observation supplies measurements that can
/// establish either a violation or recovery.
///
/// A timeout establishes a connectivity problem but provides
/// no latency or CPU measurements. It therefore cannot
/// demonstrate recovery from a measurement-related incident.
///
/// The policy makes decisions without inspecting existing
/// incidents or modifying domain state. IncidentLifecycleProcessor
/// applies these decisions to the incident collection.
///
/// Database persistence, concurrency control, and transaction
/// atomicity belong to the telemetry ingestion application service.
/// </remarks>
public static class IncidentObservationPolicy
{
    // Monitoring thresholds follow Edition 1.5.
    //
    // The thresholds represent the maximum healthy measurement.
    // A measurement must be strictly greater than its threshold
    // to indicate a violation.
    private const int LatencyWarningMs = 250;
    private const decimal CpuWarningPercent = 80m;

    /// <summary>
    /// Determines the required incident action for an observation
    /// and incident category.
    /// </summary>
    /// <param name="kind">Incident category to evaluate.</param>
    /// <param name="ping">Validated telemetry observation.</param>
    /// <returns>
    /// Keep when the observation provides insufficient evidence
    /// for a lifecycle change; EnsureOpen for a violation;
    /// Resolve for a qualifying recovery.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when ping is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when kind is not a supported incident category.
    /// </exception>
    /// <remarks>
    /// The policy does not inspect currently open incidents.
    /// It returns an action that the lifecycle processor
    /// interprets against the existing incident state.
    ///
    /// Successful observations may simultaneously require
    /// opening one incident kind and resolving another.
    /// </remarks>
    public static IncidentObservationAction Evaluate(
        IncidentKind kind,
        TelemetryPing ping)
    {
        ArgumentNullException.ThrowIfNull(ping);

        return kind switch
        {
            IncidentKind.Latency =>
                EvaluateLatency(ping),

            IncidentKind.Cpu =>
                EvaluateCpu(ping),

            IncidentKind.Connectivity =>
                // A timeout is direct evidence of a connectivity
                // failure, so an incident must remain or become open.
                //
                // Any successful observation demonstrates restored
                // connectivity, regardless of its latency or CPU
                // measurements. Measurement-related incidents are
                // evaluated separately and may remain open.
                ping.Outcome == PingOutcome.Timeout
                    ? IncidentObservationAction.EnsureOpen
                    : IncidentObservationAction.Resolve,

            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Unsupported incident category.")
        };
    }

    /// <summary>
    /// Evaluates whether latency requires an incident
    /// to open, remain unchanged, or resolve.
    /// </summary>
    /// <param name="ping">Validated telemetry observation.</param>
    /// <returns>
    /// Keep for timeout observations, EnsureOpen when latency
    /// exceeds 250 milliseconds, or Resolve otherwise.
    /// </returns>
    private static IncidentObservationAction EvaluateLatency(
        TelemetryPing ping)
    {
        // A timeout does not contain a latency measurement.
        // Missing data is not evidence that high latency has
        // recovered, so preserve the existing incident state.
        if (ping.Outcome == PingOutcome.Timeout)
            return IncidentObservationAction.Keep;

        // Exactly 250 ms is within the accepted threshold.
        // Only values above 250 ms indicate a latency violation.
        return ping.LatencyMs!.Value > LatencyWarningMs
            ? IncidentObservationAction.EnsureOpen
            : IncidentObservationAction.Resolve;
    }

    /// <summary>
    /// Evaluates whether CPU utilization requires an incident
    /// to open, remain unchanged, or resolve.
    /// </summary>
    /// <param name="ping">Validated telemetry observation.</param>
    /// <returns>
    /// Keep for timeout observations, EnsureOpen when CPU
    /// utilization exceeds 80 percent, or Resolve otherwise.
    /// </returns>
    private static IncidentObservationAction EvaluateCpu(
        TelemetryPing ping)
    {
        // A timeout contains no CPU measurement. Resolving an
        // existing CPU incident would therefore claim recovery
        // without any supporting measurement.
        if (ping.Outcome == PingOutcome.Timeout)
            return IncidentObservationAction.Keep;

        // Exactly 80 percent remains within the healthy boundary.
        // A violation requires CPU utilization above 80 percent.
        return ping.CpuPercent!.Value > CpuWarningPercent
            ? IncidentObservationAction.EnsureOpen
            : IncidentObservationAction.Resolve;
    }
}
