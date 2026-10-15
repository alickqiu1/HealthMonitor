
using System;
using System.Threading;
using System.Threading.Tasks;
using HealthMonitor.Domain.Telemetry;

namespace HealthMonitor.Application.Telemetry;

/// <summary>
/// Provides persistence operations for simulation batch receipts
/// used by telemetry ingestion and idempotent replay.
/// </summary>
/// <remarks>
/// A simulation receipt records the identity and committed result
/// of one logical telemetry ingestion request.
///
/// The repository supports two distinct workflows:
///
/// Receipt-first replay:
/// An existing receipt is retrieved before current device
/// eligibility or UUIDv7 freshness validation.
///
/// Transactional ingestion:
/// After the shared PostgreSQL advisory lock has been acquired,
/// the receipt is queried again before new telemetry is generated.
/// A new receipt is then staged within the same unit of work as
/// telemetry observations, device state changes, and incidents.
///
/// This interface does not own database transactions, acquire
/// advisory locks, or commit changes.
///
/// Transaction management belongs to the telemetry ingestion
/// unit of work and application service.
///
/// Implementations must preserve receipt history for the
/// retention period defined in Architecture Plan Edition 1.5.
/// </remarks>
public interface ISimulationBatchRepository
{
    /// <summary>
    /// Retrieves a previously committed simulation receipt
    /// using its unique batch identifier.
    /// </summary>
    /// <param name="batchId">
    /// Batch identifier used to locate the receipt.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation token for the underlying data operation.
    /// </param>
    /// <returns>
    /// The stored simulation receipt when present;
    /// otherwise, null.
    /// </returns>
    /// <remarks>
    /// This method does not decide whether a request is an
    /// identical replay or a conflicting reuse of BatchId.
    ///
    /// The ingestion service compares the stored RequestHash
    /// with the canonical hash of the incoming request.
    ///
    /// A matching hash permits replay without creating new
    /// telemetry or modifying existing incident state.
    ///
    /// A conflicting hash must cause the ingestion service
    /// to reject the request without database modifications.
    ///
    /// The ingestion service calls this method twice when
    /// necessary:
    ///
    /// 1. Before catalog and UUIDv7 freshness validation.
    /// 2. After acquiring the transaction-scoped advisory lock.
    ///
    /// The second lookup protects against a concurrent request
    /// committing the same BatchId between the first lookup
    /// and lock acquisition.
    ///
    /// Implementations must perform an actual database lookup,
    /// not rely exclusively on an in-memory cache whose contents
    /// may be stale across application instances.
    /// </remarks>
    Task<SimulationBatch?> FindByIdAsync(
        Guid batchId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages a new simulation receipt for persistence in the
    /// current telemetry unit of work.
    /// </summary>
    /// <param name="receipt">
    /// Validated committed-receipt domain entity to stage.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when receipt is null.
    /// </exception>
    /// <remarks>
    /// This method must not independently save or commit the
    /// receipt.
    ///
    /// The receipt is staged together with telemetry pings,
    /// device state updates, and incident transitions.
    ///
    /// All staged changes must be persisted and committed
    /// through the same Telemetry database transaction.
    ///
    /// This ensures that a receipt cannot exist for a
    /// partially processed batch and that committed telemetry
    /// cannot exist without its corresponding receipt.
    ///
    /// The caller must establish that the BatchId does not
    /// already identify another committed receipt.
    ///
    /// The database primary key remains the final safeguard
    /// against duplicate BatchId values.
    ///
    /// The receipt entity contains a committed timestamp
    /// representing the batch's planned commit metadata.
    /// Its presence in the change tracker does not mean the
    /// transaction has already committed successfully.
    /// </remarks>
    void Add(SimulationBatch receipt);
}
