using HealthMonitor.Domain.Telemetry;

namespace HealthMonitor.Api.Contracts.V1;

/// <summary>V1 server page; optional measurements remain explicit nulls and all timestamps map to UTC DateTime for Z output.</summary>
/// <typeparam name="T">Concrete V1 read DTO.</typeparam>
/// <param name="Items">Selected immutable records.</param>
/// <param name="Page">One-based requested page.</param>
/// <param name="PageSize">Requested capacity.</param>
/// <param name="TotalCount">Filtered count before pagination.</param>
/// <param name="AsOfUtc">Server evaluation time with DateTimeKind.Utc.</param>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount, DateTime AsOfUtc);

/// <summary>V1 inventory and read-time health; no persistence or internal retirement flag is exposed.</summary>
/// <param name="Id">Stable catalog identity.</param>
/// <param name="AssetTag">Canonical asset tag.</param>
/// <param name="DisplayName">Inventory display name.</param>
/// <param name="DeviceType">Inventory category.</param>
/// <param name="SiteCode">Inventory site.</param>
/// <param name="Health">Canonical Domain health token.</param>
/// <param name="LastObservedAtUtc">UTC observation time or null without state.</param>
/// <param name="LatencyMs">Latest success latency, including stale values; null on timeout/absence.</param>
/// <param name="CpuPercent">Latest success CPU, including stale values; null on timeout/absence.</param>
public record DeviceRowDto(Guid Id, string AssetTag, string DisplayName, string DeviceType, string SiteCode,
    HealthState Health, DateTime? LastObservedAtUtc, int? LatencyMs, decimal? CpuPercent);

/// <summary>Flat V1 detail shape, including known retired inventory and persisted last-success history.</summary>
/// <param name="Id">Stable catalog identity.</param>
/// <param name="AssetTag">Inventory tag.</param>
/// <param name="DisplayName">Inventory name.</param>
/// <param name="DeviceType">Inventory category.</param>
/// <param name="SiteCode">Inventory site.</param>
/// <param name="Health">Read-time health, independent of retirement.</param>
/// <param name="LastObservedAtUtc">UTC observation or null.</param>
/// <param name="LatencyMs">Latest optional latency.</param>
/// <param name="CpuPercent">Latest optional CPU.</param>
/// <param name="CreatedAtUtc">UTC catalog creation.</param>
/// <param name="RetiredAtUtc">UTC retirement or null while active.</param>
/// <param name="LastSuccessfulAtUtc">UTC latest success, preserved through timeout, or null.</param>
/// <param name="AsOfUtc">UTC evaluation instant.</param>
public sealed record DeviceDetailDto(Guid Id, string AssetTag, string DisplayName, string DeviceType, string SiteCode,
    HealthState Health, DateTime? LastObservedAtUtc, int? LatencyMs, decimal? CpuPercent, DateTime CreatedAtUtc,
    DateTime? RetiredAtUtc, DateTime? LastSuccessfulAtUtc, DateTime AsOfUtc)
    : DeviceRowDto(Id, AssetTag, DisplayName, DeviceType, SiteCode, Health, LastObservedAtUtc, LatencyMs, CpuPercent);

/// <summary>V1 observation facts without internal receipt references.</summary>
/// <param name="Id">Observation identity.</param>
/// <param name="DeviceId">Catalog identity.</param>
/// <param name="ObservedAtUtc">UTC server observation.</param>
/// <param name="Outcome">Success or Timeout token.</param>
/// <param name="LatencyMs">Optional latency.</param>
/// <param name="CpuPercent">Optional CPU.</param>
public sealed record TelemetryPingDto(Guid Id, Guid DeviceId, DateTime ObservedAtUtc, PingOutcome Outcome, int? LatencyMs, decimal? CpuPercent);

/// <summary>V1 incident facts with a plain-text summary and optional resolution.</summary>
/// <param name="Id">Incident identity.</param>
/// <param name="DeviceId">Catalog identity.</param>
/// <param name="Kind">Canonical Domain kind.</param>
/// <param name="OpenedAtUtc">UTC opening.</param>
/// <param name="ResolvedAtUtc">UTC resolution or null while open.</param>
/// <param name="Summary">Stored plain-text summary.</param>
public sealed record IncidentDto(Guid Id, Guid DeviceId, IncidentKind Kind, DateTime OpenedAtUtc, DateTime? ResolvedAtUtc, string Summary);

/// <summary>Global active-fleet metrics, independent of device-grid filters.</summary>
/// <param name="Total">Active catalog size.</param>
/// <param name="Healthy">Fresh healthy count.</param>
/// <param name="Degraded">Fresh degraded count.</param>
/// <param name="Offline">Stale/timeout count.</param>
/// <param name="Unknown">No-state count.</param>
/// <param name="AverageLatencyMs">Fractional fresh-success mean or null without samples.</param>
/// <param name="SampleCount">Fresh-success sample count.</param>
/// <param name="AsOfUtc">UTC evaluation instant.</param>
public sealed record MetricsSummaryDto(int Total, int Healthy, int Degraded, int Offline, int Unknown,
    decimal? AverageLatencyMs, int SampleCount, DateTime AsOfUtc);
