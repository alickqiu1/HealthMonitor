using HealthMonitor.Application.IntegrationContracts;

namespace HealthMonitor.Application.Device;

/// <summary>Provides read-only Device inventory projections for application consumers.</summary>
/// <remarks>
/// Operations do not write or determine ingestion eligibility. Use one scoped instance
/// sequentially; its persistence context is not safe for concurrent operations.
/// Provider failures propagate to the caller for handling at the application boundary.
/// The active limit bounds in-memory fleet composition in the API. Overflow is an error
/// because a partial catalog would misrepresent fleet totals and pagination.
/// </remarks>
public interface IDeviceCatalogQueries
{
    /// <summary>The Edition 1.5 active-fleet composition capacity.</summary>
    const int MaximumActiveDevices = 1_000;

    /// <summary>Reads one known registration and lifecycle metadata, including retired inventory.</summary>
    /// <param name="deviceId">Explicit identity; an empty UUID is unknown without database access.</param>
    /// <param name="cancellationToken">Cancellation checked first, including for an empty UUID.</param>
    /// <returns>Immutable detail, or null for an unknown identity. Provider failures propagate.</returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    Task<DeviceCatalogDetail?> GetDetailAsync(Guid deviceId, CancellationToken cancellationToken = default);

    /// <summary>Looks up known registrations without excluding retired devices.</summary>
    /// <param name="deviceIds">Non-null requested IDs; duplicates are collapsed. No ingestion batch limit applies here.</param>
    /// <param name="cancellationToken">Cancellation for the complete operation, including empty requests.</param>
    /// <returns>
    /// One registration per known ID in ascending UUID order, independent of input order.
    /// Unknown IDs (including an unregistered empty UUID) are omitted. Empty input returns
    /// an empty list without database access; mixed requests return only known IDs.
    /// Retirement is preserved so callers can apply their own eligibility policy.
    /// </returns>
    /// <exception cref="ArgumentNullException">The requested collection is null.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    Task<IReadOnlyList<DeviceRegistration>> GetByIdsAsync(
        IReadOnlyCollection<Guid> deviceIds,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the complete active catalog within the supported fleet capacity.</summary>
    /// <param name="cancellationToken">Cancellation for the database operation.</param>
    /// <returns>
    /// Zero through 1,000 non-retired registrations in ascending UUID order.
    /// The result is complete for this query's database snapshot, never silently truncated.
    /// </returns>
    /// <exception cref="DeviceCatalogCapacityExceededException">More than 1,000 active devices exist.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    Task<IReadOnlyList<DeviceRegistration>> GetActiveAsync(
        CancellationToken cancellationToken = default);
}
