namespace HealthMonitor.Application.Telemetry;

/// <summary>Transfers stable, validated inputs from Application orchestration to atomic persistence.</summary>
/// <remarks>
/// Construction is internal to Application; Infrastructure consumes facts without reimplementing
/// request validation. Neither caller-owned arrays nor their mutable collection views escape here.
/// Current eligibility is a preliminary Device read, not a distributed transaction guarantee.
/// </remarks>
public sealed class TelemetryIngestionBatch
{
    /// <summary>Gets the caller's logical request identity, reused for an uncertain-outcome retry.</summary>
    public Guid BatchId { get; }

    /// <summary>Gets the exact supported scenario token used in the canonical hash.</summary>
    public string Scenario { get; }

    /// <summary>Gets the canonical SHA-256 request hash for receipt comparison.</summary>
    public string RequestHash { get; }

    /// <summary>Gets a read-only, defensively copied collection of distinct eligible device IDs.</summary>
    public IReadOnlyList<Guid> DeviceIds { get; }

    /// <summary>Gets the server UTC time captured when this attempt entered ingestion.</summary>
    /// <remarks>
    /// This is not the UUID timestamp or observation time. Observations are timestamped after
    /// locking; the receipt's planned commit time must not precede this request timestamp.
    /// Replays return the original receipt rather than replacing its metadata with this attempt's time.
    /// </remarks>
    public DateTimeOffset RequestedAtUtc { get; }

    /// <summary>Captures inputs only after Application has completed all new-request validation.</summary>
    /// <param name="request">The already snapshotted and validated request.</param>
    /// <param name="requestHash">Hash calculated from that same snapshot.</param>
    /// <param name="requestedAtUtc">Server UTC time captured on entry to the Application service.</param>
    internal TelemetryIngestionBatch(
        TelemetryIngestionRequest request, string requestHash, DateTimeOffset requestedAtUtc)
    {
        BatchId = request.BatchId;
        Scenario = request.Scenario;
        RequestHash = requestHash;
        DeviceIds = Array.AsReadOnly(request.DeviceIds.ToArray());
        RequestedAtUtc = requestedAtUtc;
    }
}
