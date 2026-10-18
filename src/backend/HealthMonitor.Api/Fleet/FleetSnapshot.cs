using HealthMonitor.Domain.Telemetry;

namespace HealthMonitor.Api.Fleet;

/// <summary>Complete bounded active fleet evaluated at one instant; separate module reads are not a serializable snapshot.</summary>
/// <param name="AsOfUtc">Health evaluation instant captured before either query.</param>
/// <param name="Rows">Frozen active rows before future filtering or pagination.</param>
/// <param name="Metrics">Global metrics from the same complete rows.</param>
internal sealed record FleetSnapshot(DateTimeOffset AsOfUtc, IReadOnlyList<FleetRow> Rows, FleetMetrics Metrics);

/// <summary>Inventory and computed health without exposing persistence entities or future HTTP contracts.</summary>
/// <param name="DeviceId">Active catalog identity.</param>
/// <param name="DisplayName">Inventory display name.</param>
/// <param name="AssetTag">Inventory asset tag.</param>
/// <param name="SiteCode">Inventory site.</param>
/// <param name="DeviceType">Inventory category.</param>
/// <param name="Status">Read-time Domain classification.</param>
/// <param name="LastObservedAtUtc">Latest observation; null for absent state.</param>
/// <param name="LatencyMs">Latest measurement, retained when stale; null for timeout or absence.</param>
/// <param name="CpuPercent">Latest measurement, retained when stale; null for timeout or absence.</param>
internal sealed record FleetRow(Guid DeviceId, string DisplayName, string AssetTag, string SiteCode,
    string DeviceType, HealthState Status, DateTimeOffset? LastObservedAtUtc, int? LatencyMs, decimal? CpuPercent);

/// <summary>Global active counts and fractional mean latency for fresh successes only.</summary>
/// <param name="Total">Complete active catalog size.</param>
/// <param name="Healthy">Fresh successes within thresholds.</param>
/// <param name="Degraded">Fresh successes exceeding a threshold.</param>
/// <param name="Offline">Stale observations or timeouts.</param>
/// <param name="Unknown">Registrations without state.</param>
/// <param name="AverageLatencyMs">Decimal mean; null when there are no samples.</param>
/// <param name="SampleCount">Fresh successes contributing to the mean.</param>
internal sealed record FleetMetrics(int Total, int Healthy, int Degraded, int Offline, int Unknown,
    decimal? AverageLatencyMs, int SampleCount);
