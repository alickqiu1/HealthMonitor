using HealthMonitor.Application.IntegrationContracts;

namespace HealthMonitor.Application.Telemetry;

/// <summary>Reads immutable latest-observation projections without deciding catalog eligibility or computed health.</summary>
/// <remarks>Use sequentially within a scope; the underlying context is not safe for concurrent operations. Provider failures propagate unchanged.</remarks>
public interface IDeviceHealthQueries
{
    /// <summary>Maximum distinct requested IDs, matching the supported bounded fleet without referencing the Device module.</summary>
    const int MaximumDeviceIdsPerQuery = 1_000;

    /// <summary>Returns persisted latest states for the requested identities only, without tracking entities.</summary>
    /// <param name="deviceIds">Non-null IDs; snapshotted before asynchronous work. Duplicates collapse; unknown/empty IDs have no result.</param>
    /// <param name="cancellationToken">Cancellation checked even for empty input before any empty result is returned.</param>
    /// <returns>
    /// At most one snapshot per requested state, ordered by database UUID ascending. This transport order does not
    /// define the future grid's explicit tie-breaking policy. Missing states are omitted, not invented observations.
    /// </returns>
    /// <exception cref="ArgumentNullException">The requested collection is null.</exception>
    /// <exception cref="ArgumentException">More than 1,000 distinct IDs were requested, including any empty ID.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    Task<IReadOnlyList<DeviceTelemetrySnapshot>> GetLatestByDeviceIdsAsync(
        IReadOnlyCollection<Guid> deviceIds, CancellationToken cancellationToken = default);
}
