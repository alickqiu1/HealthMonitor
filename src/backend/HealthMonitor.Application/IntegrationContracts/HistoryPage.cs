using HealthMonitor.Application.Telemetry;
using HealthMonitor.Domain.Telemetry;

namespace HealthMonitor.Application.IntegrationContracts;

/// <summary>Immutable projected page envelope; producers freeze Items and count/page share one database snapshot.</summary>
/// <typeparam name="T">One of the purpose-built Telemetry history projections.</typeparam>
/// <param name="Items">Frozen page items, never tracked entities.</param>
/// <param name="Paging">Requested validated coordinates, including out-of-range pages.</param>
/// <param name="TotalCount">True filtered count before pagination, using Int64.</param>
public sealed record HistoryPage<T>(IReadOnlyList<T> Items, HistoryPaging Paging, long TotalCount);

/// <summary>Read-only observation facts without a receipt, state entity, or Device dependency.</summary>
/// <param name="Id">Observation identity and descending tie-breaker.</param>
/// <param name="DeviceId">Requested Telemetry identity.</param>
/// <param name="ObservedAtUtc">Server observation time.</param>
/// <param name="Outcome">Persisted observation outcome.</param>
/// <param name="LatencyMs">Success latency or null on timeout.</param>
/// <param name="CpuPercent">Success CPU or null on timeout.</param>
public sealed record TelemetryHistoryItem(Guid Id, Guid DeviceId, DateTimeOffset ObservedAtUtc,
    PingOutcome Outcome, int? LatencyMs, decimal? CpuPercent);

/// <summary>Read-only incident lifecycle facts; summary remains plain text for later HTTP encoding.</summary>
/// <param name="Id">Incident identity and descending tie-breaker.</param>
/// <param name="DeviceId">Requested Telemetry identity.</param>
/// <param name="Kind">Domain incident kind.</param>
/// <param name="OpenedAtUtc">Opening time.</param>
/// <param name="ResolvedAtUtc">Resolution time or null while open.</param>
/// <param name="Summary">Stored plain-text Domain summary.</param>
public sealed record IncidentHistoryItem(Guid Id, Guid DeviceId, IncidentKind Kind, DateTimeOffset OpenedAtUtc,
    DateTimeOffset? ResolvedAtUtc, string Summary);
