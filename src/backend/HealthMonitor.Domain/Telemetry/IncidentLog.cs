
using System;

namespace HealthMonitor.Domain.Telemetry;

/// <summary>
/// Tracks one synthetic device health incident
/// from its opening observation through recovery.
/// </summary>
public sealed class IncidentLog
{
    public Guid Id { get; private set; }
    public Guid DeviceId { get; private set; }
    public IncidentKind Kind { get; private set; }

    public DateTimeOffset OpenedAtUtc { get; private set; }
    public DateTimeOffset? ResolvedAtUtc { get; private set; }

    public Guid? OpenedByPingId { get; private set; }
    public Guid? ResolvedByPingId { get; private set; }

    public string Summary { get; private set; } = string.Empty;

    // Used by EF Core during materialization.
    private IncidentLog() { }

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
    /// Resolves an open incident after a qualifying
    /// recovery observation.
    /// </summary>
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
