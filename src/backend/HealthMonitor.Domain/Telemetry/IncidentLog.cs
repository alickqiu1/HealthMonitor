
using System;

namespace HealthMonitor.Domain.Telemetry;

/// <summary>
/// Represents a synthetic device health incident from detection
/// through resolution.
/// </summary>
/// <remarks>
/// This entity belongs to the Telemetry bounded context.
///
/// An incident records one occurrence of a latency, CPU, or
/// connectivity problem. It retains the opening observation,
/// an optional recovery observation, and the timestamps that
/// describe its lifecycle.
///
/// Incidents are opened and resolved through explicit domain
/// operations. Once resolved, an incident cannot be reopened.
///
/// The application service determines when observations qualify
/// for opening or resolving incidents. This entity enforces the
/// validity of its own state transitions.
///
/// Device references are scalar identifiers. No cross-module
/// navigation property or foreign key to the Device schema
/// is required.
/// </remarks>
public sealed class IncidentLog
{
    /// <summary>
    /// Gets the unique identifier of the incident.
    /// </summary>
    public Guid Id { get; private set; }

    /// <summary>
    /// Gets the identifier of the affected device.
    /// </summary>
    public Guid DeviceId { get; private set; }

    /// <summary>
    /// Gets the category of health problem represented by the incident.
    /// </summary>
    public IncidentKind Kind { get; private set; }

    /// <summary>
    /// Gets the UTC timestamp when the incident was opened.
    /// </summary>
    public DateTimeOffset OpenedAtUtc { get; private set; }

    /// <summary>
    /// Gets the UTC resolution timestamp, or null while the
    /// incident remains open.
    /// </summary>
    public DateTimeOffset? ResolvedAtUtc { get; private set; }

    /// <summary>
    /// Gets the identifier of the observation that opened the incident.
    /// </summary>
    /// <remarks>
    /// A new incident requires a non-empty opening observation ID.
    /// The stored property is nullable to support persistence and
    /// possible retention of historical incident records.
    /// </remarks>
    public Guid? OpenedByPingId { get; private set; }

    /// <summary>
    /// Gets the identifier of the observation that resolved
    /// the incident, or null while it remains open.
    /// </summary>
    public Guid? ResolvedByPingId { get; private set; }

    /// <summary>
    /// Gets the human-readable incident summary.
    /// </summary>
    /// <remarks>
    /// Leading and trailing whitespace is removed at creation.
    /// The application service supplies a summary based on the
    /// incident kind and the observed health condition.
    /// </remarks>
    public string Summary { get; private set; } = string.Empty;

    // Required by EF Core for entity materialization.
    // Application code must use the Open factory method.
    private IncidentLog() { }

    /// <summary>
    /// Creates and validates a new open incident.
    /// </summary>
    /// <param name="id">Non-empty incident identifier.</param>
    /// <param name="deviceId">Non-empty affected device identifier.</param>
    /// <param name="kind">Defined incident category.</param>
    /// <param name="openedAtUtc">
    /// Non-default opening timestamp with a zero UTC offset.
    /// </param>
    /// <param name="openedByPingId">
    /// Non-empty identifier of the opening observation.
    /// </param>
    /// <param name="summary">Required human-readable summary.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when an identifier, timestamp, or summary is invalid.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the incident kind is undefined.
    /// </exception>
    /// <remarks>
    /// All constructor inputs are validated before assigning state.
    /// A newly created incident has no resolution timestamp
    /// or resolving observation.
    /// </remarks>
    private IncidentLog(
        Guid id,
        Guid deviceId,
        IncidentKind kind,
        DateTimeOffset openedAtUtc,
        Guid openedByPingId,
        string summary)
    {
        if (id == Guid.Empty)
            throw new ArgumentException(
                "Incident ID cannot be empty.", nameof(id));

        if (deviceId == Guid.Empty)
            throw new ArgumentException(
                "Device ID cannot be empty.", nameof(deviceId));

        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));

        if (openedAtUtc == default ||
            openedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "A valid UTC opening timestamp is required.",
                nameof(openedAtUtc));
        }

        if (openedByPingId == Guid.Empty)
            throw new ArgumentException(
                "Opening ping ID cannot be empty.",
                nameof(openedByPingId));

        if (string.IsNullOrWhiteSpace(summary))
            throw new ArgumentException(
                "An incident summary is required.",
                nameof(summary));

        string normalizedSummary = summary.Trim();

        Id = id;
        DeviceId = deviceId;
        Kind = kind;
        OpenedAtUtc = openedAtUtc;
        OpenedByPingId = openedByPingId;
        Summary = normalizedSummary;
    }

    /// <summary>
    /// Opens a new incident for a monitored device.
    /// </summary>
    /// <param name="id">Non-empty incident identifier.</param>
    /// <param name="deviceId">Non-empty device identifier.</param>
    /// <param name="kind">Defined incident category.</param>
    /// <param name="openedAtUtc">
    /// Non-default opening timestamp with a zero UTC offset.
    /// </param>
    /// <param name="openedByPingId">
    /// Non-empty identifier of the observation that triggered the incident.
    /// </param>
    /// <param name="summary">Required incident summary.</param>
    /// <returns>A new, unresolved incident.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when an identifier, timestamp, or summary is invalid.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the incident kind is undefined.
    /// </exception>
    /// <remarks>
    /// The caller is responsible for applying the monitoring policy
    /// and deciding whether the observation warrants an incident.
    /// This factory validates the incident's own invariants.
    /// </remarks>
    public static IncidentLog Open(
        Guid id,
        Guid deviceId,
        IncidentKind kind,
        DateTimeOffset openedAtUtc,
        Guid openedByPingId,
        string summary)
    {
        return new IncidentLog(
            id,
            deviceId,
            kind,
            openedAtUtc,
            openedByPingId,
            summary);
    }

    /// <summary>
    /// Resolves an open incident after a qualifying recovery observation.
    /// </summary>
    /// <param name="resolvedAtUtc">
    /// Non-default UTC timestamp that must not precede incident opening.
    /// </param>
    /// <param name="resolvedByPingId">
    /// Non-empty identifier of the recovery observation.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when the resolution timestamp or observation ID is invalid.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the incident has already been resolved,
    /// provided the supplied resolution arguments are valid.
    /// </exception>
    /// <remarks>
    /// Resolution is a one-way state transition.
    ///
    /// The method validates the timestamp and observation identifier
    /// before checking whether the incident has already been resolved.
    ///
    /// On successful resolution, both resolution fields are assigned.
    /// Failed validation leaves the incident unchanged.
    /// </remarks>
    public void Resolve(
        DateTimeOffset resolvedAtUtc,
        Guid resolvedByPingId)
    {
        if (resolvedAtUtc == default ||
            resolvedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "A valid UTC resolution timestamp is required.",
                nameof(resolvedAtUtc));
        }

        if (resolvedAtUtc < OpenedAtUtc)
            throw new ArgumentException(
                "Resolution cannot precede incident opening.",
                nameof(resolvedAtUtc));

        if (resolvedByPingId == Guid.Empty)
            throw new ArgumentException(
                "Resolution ping ID cannot be empty.",
                nameof(resolvedByPingId));

        if (ResolvedAtUtc.HasValue)
            throw new InvalidOperationException(
                "This incident has already been resolved.");

        ResolvedAtUtc = resolvedAtUtc;
        ResolvedByPingId = resolvedByPingId;
    }
}
