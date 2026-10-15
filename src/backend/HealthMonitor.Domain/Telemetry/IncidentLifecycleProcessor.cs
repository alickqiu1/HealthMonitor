
using System;
using System.Collections.Generic;
using System.Linq;

namespace HealthMonitor.Domain.Telemetry;

/// <summary>
/// Applies telemetry observation decisions to the incident
/// lifecycle of a monitored device.
/// </summary>
/// <remarks>
/// Coordinates opening, preserving, and resolving Latency,
/// Cpu, and Connectivity incidents.
///
/// Business lifecycle rules:
///
/// - One unresolved incident is permitted per device and kind.
/// - Repeated violations preserve the existing open incident.
/// - A recovered condition resolves its corresponding incident.
/// - An unrelated condition may remain open after recovery.
/// - Resolved incidents remain in history.
/// - A new violation after resolution creates a new incident.
///
/// IncidentObservationPolicy determines whether the latest
/// observation represents a violation, recovery, or insufficient
/// evidence to change an incident.
///
/// This service operates only on in-memory domain entities.
/// It does not query PostgreSQL or manage database transactions.
///
/// The ingestion service must enforce observation ordering,
/// supply the relevant incident collection, and persist telemetry,
/// state, incidents, and receipts atomically.
///
/// Database uniqueness constraints provide an additional safeguard
/// against concurrent duplicate incident creation.
/// </remarks>
public sealed class IncidentLifecycleProcessor
{
    /// <summary>
    /// Applies one telemetry observation to the incident
    /// collection for its device.
    /// </summary>
    /// <param name="ping">
    /// Validated observation being processed.
    /// </param>
    /// <param name="incidents">
    /// Mutable collection of incidents belonging to the
    /// observed device, including currently open incidents
    /// and optionally resolved historical incidents.
    /// </param>
    /// <param name="createIncidentId">
    /// Factory supplying a new non-empty incident ID when
    /// a previously absent incident must be opened.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when a required argument is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when an incident is null or belongs to another device.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown for duplicate open incidents, invalid generated
    /// IDs, conflicting IDs, unsupported open incident kinds,
    /// or invalid resolution chronology.
    /// </exception>
    /// <remarks>
    /// Processing follows four stages:
    ///
    /// 1. Validate the supplied incident collection.
    /// 2. Evaluate the observation and prepare new incidents.
    /// 3. Validate and apply recovery transitions.
    /// 4. Append newly opened incidents.
    ///
    /// New incidents are prepared before existing incidents
    /// are modified. This reduces partial-mutation risks when
    /// validation or identifier generation fails.
    ///
    /// The method assumes observations are processed in
    /// chronological order. The ingestion service is responsible
    /// for enforcing this requirement.
    ///
    /// In-memory processing is not a substitute for transaction
    /// atomicity. Persistence must occur within the ingestion
    /// service's database transaction.
    /// </remarks>
    public void Process(
        TelemetryPing ping,
        IList<IncidentLog> incidents,
        Func<Guid> createIncidentId)
    {
        ArgumentNullException.ThrowIfNull(ping);
        ArgumentNullException.ThrowIfNull(incidents);
        ArgumentNullException.ThrowIfNull(createIncidentId);

        // Every supplied incident must belong to the observed device.
        //
        // Processing a mixed-device collection could accidentally
        // resolve another device's incident or incorrectly conclude
        // that an incident is already open.
        //
        // Reject the entire collection before making changes.
        if (incidents.Any(i => i is null || i.DeviceId != ping.DeviceId))
        {
            throw new ArgumentException(
                "All incidents must belong to the observed device.",
                nameof(incidents));
        }

        // Only unresolved incidents participate in current
        // incident lifecycle decisions.
        //
        // Resolved incidents represent historical occurrences
        // and must not block new incidents of the same kind.
        var openIncidents = incidents
            .Where(i => !i.ResolvedAtUtc.HasValue)
            .ToList();

        // Each device may have at most one unresolved incident
        // of a given kind.
        //
        // Multiple open incidents of the same kind represent an
        // inconsistent domain state. Silently choosing one would
        // conceal the problem and could produce incorrect history.
        if (openIncidents
            .GroupBy(i => i.Kind)
            .Any(group => group.Count() > 1))
        {
            throw new InvalidOperationException(
                "Multiple open incidents of the same kind exist.");
        }

        // Evaluate every incident category independently.
        //
        // A device may have high latency and high CPU at the same
        // time. Recovery from one condition must not automatically
        // resolve the other.
        //
        // A successful observation also demonstrates connectivity
        // recovery, even when latency or CPU remains unhealthy.
        IncidentObservationAction latencyAction =
            IncidentObservationPolicy.Evaluate(
                IncidentKind.Latency,
                ping);

        IncidentObservationAction cpuAction =
            IncidentObservationPolicy.Evaluate(
                IncidentKind.Cpu,
                ping);

        IncidentObservationAction connectivityAction =
            IncidentObservationPolicy.Evaluate(
                IncidentKind.Connectivity,
                ping);

        // Prepare new incidents before modifying existing records.
        //
        // A single observation may open both Latency and Cpu
        // incidents. If generating the second incident ID fails,
        // we should not have already appended the first one.
        //
        // Pending incidents are added only after all opening
        // validations and resolution chronology checks succeed.
        var pendingIncidents = new List<IncidentLog>();

        PrepareOpening(
            IncidentKind.Latency,
            latencyAction,
            "High latency detected",
            ping,
            openIncidents,
            pendingIncidents,
            createIncidentId,
            incidents);

        PrepareOpening(
            IncidentKind.Cpu,
            cpuAction,
            "High CPU utilization detected",
            ping,
            openIncidents,
            pendingIncidents,
            createIncidentId,
            incidents);

        PrepareOpening(
            IncidentKind.Connectivity,
            connectivityAction,
            "Device connectivity timeout detected",
            ping,
            openIncidents,
            pendingIncidents,
            createIncidentId,
            incidents);

        // Validate all applicable recovery transitions before
        // resolving any existing incidents.
        //
        // An observation cannot legitimately resolve an incident
        // that had not yet opened.
        //
        // Prevalidation prevents a valid first resolution followed
        // by an invalid second resolution from leaving the collection
        // partially updated.
        foreach (IncidentLog incident in openIncidents)
        {
            IncidentObservationAction action =
                incident.Kind switch
                {
                    IncidentKind.Latency => latencyAction,
                    IncidentKind.Cpu => cpuAction,
                    IncidentKind.Connectivity => connectivityAction,
                    _ => throw new InvalidOperationException(
                        "Unsupported open incident category.")
                };

            if (action == IncidentObservationAction.Resolve &&
                ping.ObservedAtUtc < incident.OpenedAtUtc)
            {
                throw new InvalidOperationException(
                    "Observation precedes an open incident.");
            }
        }

        // Resolve existing incidents only when the latest
        // observation provides evidence of recovery.
        //
        // A successful observation can resolve Connectivity
        // while leaving Latency or Cpu open if those measurements
        // still exceed their respective thresholds.
        //
        // Timeout observations do not resolve measurement-related
        // incidents because the required measurements are absent.
        ResolveIfRequired(
            IncidentKind.Latency,
            latencyAction,
            ping,
            openIncidents);

        ResolveIfRequired(
            IncidentKind.Cpu,
            cpuAction,
            ping,
            openIncidents);

        ResolveIfRequired(
            IncidentKind.Connectivity,
            connectivityAction,
            ping,
            openIncidents);

        // Append newly opened incidents after successful validation.
        //
        // Historical incidents remain in the collection so separate
        // occurrences of the same problem remain distinguishable.
        foreach (IncidentLog incident in pendingIncidents)
        {
            incidents.Add(incident);
        }
    }

    /// <summary>
    /// Prepares a new incident when the observation requires one
    /// and no matching open incident exists.
    /// </summary>
    /// <param name="kind">Incident category being evaluated.</param>
    /// <param name="action">Policy decision for the category.</param>
    /// <param name="summary">System-generated incident summary.</param>
    /// <param name="ping">Observation triggering the decision.</param>
    /// <param name="openIncidents">Currently unresolved incidents.</param>
    /// <param name="pendingIncidents">New incidents awaiting insertion.</param>
    /// <param name="createIncidentId">Incident identifier factory.</param>
    /// <param name="allIncidents">All supplied incident records.</param>
    /// <remarks>
    /// An existing open incident represents an ongoing occurrence
    /// of the same health problem.
    ///
    /// Repeated violating observations must preserve its original
    /// incident ID, opening timestamp, and triggering observation.
    ///
    /// A new incident is appropriate only when no unresolved
    /// incident of that kind exists.
    ///
    /// This also allows a new occurrence after a previous incident
    /// has been resolved, without overwriting historical records.
    /// </remarks>
    private static void PrepareOpening(
        IncidentKind kind,
        IncidentObservationAction action,
        string summary,
        TelemetryPing ping,
        IReadOnlyCollection<IncidentLog> openIncidents,
        IList<IncidentLog> pendingIncidents,
        Func<Guid> createIncidentId,
        IEnumerable<IncidentLog> allIncidents)
    {
        // Keep and Resolve never require a new incident.
        //
        // In particular, a recovery observation with no existing
        // open incident is a normal no-op, not an error.
        if (action != IncidentObservationAction.EnsureOpen)
            return;

        // A repeated violation continues the existing incident.
        //
        // Creating another incident would artificially split one
        // unresolved problem into multiple incident records and
        // misrepresent its duration.
        //
        // Preserve the original opening ping and timestamp.
        if (openIncidents.Any(i => i.Kind == kind))
            return;

        Guid incidentId = createIncidentId();

        // A new incident must not reuse the identity of either
        // a historical incident or another incident prepared
        // during this same observation.
        //
        // Validate before constructing or appending the incident
        // to avoid introducing an invalid identity.
        if (incidentId == Guid.Empty ||
            allIncidents.Any(i => i.Id == incidentId) ||
            pendingIncidents.Any(i => i.Id == incidentId))
        {
            throw new InvalidOperationException(
                "Incident identifier must be non-empty and unique.");
        }

        // The violating observation establishes the beginning
        // of this incident occurrence.
        //
        // Use its timestamp and ping ID rather than the current
        // processing time, which could differ from observation time.
        //
        // Summary text is generated by trusted application logic,
        // not copied from arbitrary user-supplied HTML.
        IncidentLog incident = IncidentLog.Open(
            incidentId,
            ping.DeviceId,
            kind,
            ping.ObservedAtUtc,
            ping.Id,
            summary);

        pendingIncidents.Add(incident);
    }

    /// <summary>
    /// Resolves an existing open incident when the latest
    /// observation demonstrates recovery of that condition.
    /// </summary>
    /// <param name="kind">Incident category being evaluated.</param>
    /// <param name="action">Policy decision for the category.</param>
    /// <param name="ping">Recovery observation.</param>
    /// <param name="openIncidents">Currently unresolved incidents.</param>
    /// <remarks>
    /// Resolution is conditional on both the policy decision
    /// and the existence of a matching open incident.
    ///
    /// If no incident exists, there is nothing to resolve.
    ///
    /// An incident is resolved in place to preserve its identity,
    /// opening information, and historical continuity.
    /// </remarks>
    private static void ResolveIfRequired(
        IncidentKind kind,
        IncidentObservationAction action,
        TelemetryPing ping,
        IEnumerable<IncidentLog> openIncidents)
    {
        // Keep preserves the current incident state.
        // EnsureOpen is handled separately by PrepareOpening.
        if (action != IncidentObservationAction.Resolve)
            return;

        IncidentLog? incident =
            openIncidents.FirstOrDefault(i => i.Kind == kind);

        // Recovery without an existing incident is expected.
        //
        // Do not create a resolved incident merely to record
        // that a measurement was within the healthy range.
        //
        // When an incident exists, preserve its original opening
        // data and record the recovery observation separately.
        incident?.Resolve(
            ping.ObservedAtUtc,
            ping.Id);
    }
}
