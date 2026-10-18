using HealthMonitor.Domain.Telemetry;

namespace HealthMonitor.Application.IntegrationContracts;

/// <summary>Immutable persisted latest-observation facts for API composition, without exposing entities or computing health.</summary>
/// <param name="DeviceId">Telemetry identity; this projection does not establish catalog registration or retirement.</param>
/// <param name="LastObservedAtUtc">Persisted zero-offset UTC observation timestamp.</param>
/// <param name="LastSuccessfulAtUtc">Last success time, possibly null before any success; preserved across timeouts.</param>
/// <param name="LatestOutcome">Latest Success or Timeout outcome.</param>
/// <param name="LatestLatencyMs">Latest success latency in milliseconds; null on Timeout.</param>
/// <param name="LatestCpuPercent">Latest success CPU percentage; null on Timeout.</param>
/// <remarks>No observation is represented by an absent snapshot. This read is not a historical snapshot at an API evaluation time.</remarks>
public sealed record DeviceTelemetrySnapshot(Guid DeviceId, DateTimeOffset LastObservedAtUtc,
    DateTimeOffset? LastSuccessfulAtUtc, PingOutcome LatestOutcome, int? LatestLatencyMs, decimal? LatestCpuPercent);
