
using System;
using System.Threading;
using System.Threading.Tasks;
using HealthMonitor.Application.Telemetry;
using HealthMonitor.Domain.Telemetry;
using HealthMonitor.Infrastructure.Data.TelemetryContext;
using Microsoft.EntityFrameworkCore;

namespace HealthMonitor.Infrastructure.Data.TelemetryContext;

/// <summary>
/// Provides EF Core persistence operations for simulation
/// batch receipts in the Telemetry bounded context.
/// </summary>
/// <remarks>
/// Implements ISimulationBatchRepository using the existing
/// TelemetryDbContext.
///
/// Simulation receipts support idempotent ingestion:
///
/// - An existing BatchId identifies a previously committed request.
/// - A matching request hash permits replay of the saved result.
/// - A different request hash represents a conflicting replay.
/// - A missing receipt permits new ingestion after validation.
///
/// The repository performs receipt lookup and change tracking only.
/// It does not decide whether a replay is valid, validate current
/// device eligibility, or generate telemetry observations.
///
/// Transaction ownership belongs to the ingestion application
/// service and its unit of work.
///
/// The same scoped TelemetryDbContext must be used by the
/// repository and the transaction coordinator. This ensures
/// staged receipts participate in the transaction containing
/// telemetry observations, device state changes, and incidents.
///
/// The repository does not acquire advisory locks, begin
/// transactions, call SaveChanges, or commit database changes.
/// </remarks>
public sealed class SimulationBatchRepository : ISimulationBatchRepository
{
    private readonly TelemetryDbContext _context;

    /// <summary>
    /// Initializes the repository using the supplied Telemetry
    /// persistence context.
    /// </summary>
    /// <param name="context">
    /// Scoped EF Core context used for receipt queries and
    /// staging new receipt entities.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when context is null.
    /// </exception>
    public SimulationBatchRepository(TelemetryDbContext context)
    {
        _context = context
            ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Retrieves a previously committed simulation receipt
    /// by its unique batch identifier.
    /// </summary>
    /// <param name="batchId">
    /// Identifier of the receipt to retrieve.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation token for the database query.
    /// </param>
    /// <returns>
    /// The persisted receipt when found; otherwise, null.
    /// </returns>
    /// <remarks>
    /// This method always queries the database rather than
    /// searching the EF Core change tracker.
    ///
    /// Receipt-first replay requires a reliable view of
    /// previously committed operations.
    ///
    /// In particular, the ingestion service performs a second
    /// lookup after acquiring the transaction-scoped advisory
    /// lock. That lookup must be capable of observing a receipt
    /// committed by another application instance while the
    /// current request was waiting for the lock.
    ///
    /// AsNoTracking avoids returning a previously tracked entity
    /// whose state could be stale.
    ///
    /// The ingestion service is responsible for comparing
    /// RequestHash and deciding whether to replay or reject.
    /// </remarks>
    public Task<SimulationBatch?> FindByIdAsync(
        Guid batchId,
        CancellationToken cancellationToken = default)
    {
        // Always perform a database query for receipt lookup.
        //
        // A tracked or cached entity might not reflect a receipt
        // committed by another API instance after the initial
        // pre-transaction check.
        //
        // PostgreSQL READ COMMITTED isolation allows the query
        // after advisory-lock acquisition to observe transactions
        // that committed while this request was waiting.
        return _context.Set<SimulationBatch>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                receipt => receipt.BatchId == batchId,
                cancellationToken);
    }

    /// <summary>
    /// Stages a new simulation receipt in the current EF Core
    /// unit of work without immediately persisting it.
    /// </summary>
    /// <param name="receipt">
    /// Validated receipt entity representing the batch that
    /// will be committed with its telemetry changes.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when receipt is null.
    /// </exception>
    /// <remarks>
    /// This method only marks the entity for insertion.
    ///
    /// It intentionally does not call SaveChangesAsync
    /// because the receipt must be persisted atomically
    /// with telemetry observations, latest device states,
    /// and incident transitions.
    ///
    /// If ingestion fails before commit, the surrounding
    /// transaction must roll back all persisted changes.
    ///
    /// The receipt's CommittedAtUtc property represents the
    /// timestamp recorded for the batch, not evidence that
    /// the transaction has already committed successfully.
    /// </remarks>
    public void Add(SimulationBatch receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        // Stage the receipt using the same scoped DbContext
        // that participates in the ingestion transaction.
        //
        // Do not create a separate DbContext or call SaveChanges:
        // doing so could persist a receipt independently from
        // the measurements and incident changes it represents.
        _context.Set<SimulationBatch>().Add(receipt);
    }
}
