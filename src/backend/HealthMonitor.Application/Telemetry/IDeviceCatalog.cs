using HealthMonitor.Application.IntegrationContracts;

namespace HealthMonitor.Application.Telemetry;

/// <summary>Supplies inventory registrations to Telemetry without Device persistence dependencies.</summary>
/// <remarks>
/// The ingestion workflow must compare committed receipts before querying current
/// eligibility, then reject unknown or retired devices only for new batches.
/// This port retrieves facts; it does not enforce replay, eligibility, or transaction policy.
/// Calls sharing a scope must be sequential. Dependency failures propagate to the caller.
/// </remarks>
public interface IDeviceCatalog
{
    /// <summary>Retrieves known active and retired registrations for the requested IDs.</summary>
    /// <param name="deviceIds">Non-null IDs; duplicates collapse and unknown IDs are omitted.</param>
    /// <param name="cancellationToken">Cancellation propagated through the catalog query.</param>
    /// <returns>
    /// Known registrations once each in ascending UUID order, preserving retirement.
    /// Empty input produces an empty list without database access. No ingestion batch-size
    /// policy is applied by this port; the ingestion workflow owns request validation.
    /// </returns>
    /// <exception cref="ArgumentNullException">The requested collection is null.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    Task<IReadOnlyList<DeviceRegistration>> GetByIdsAsync(
        IReadOnlyCollection<Guid> deviceIds,
        CancellationToken cancellationToken = default);
}
