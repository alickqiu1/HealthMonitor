using System.Data;
using System.Runtime.ExceptionServices;
using HealthMonitor.Application.Telemetry;
using HealthMonitor.Domain.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HealthMonitor.Infrastructure.Data.TelemetryContext;

/// <summary>Persists one ingestion attempt using the scoped Telemetry context and shared database lock.</summary>
/// <remarks>
/// The context is exclusive to one attempt. Admission rejects existing tracking or transactions;
/// after an admitted failure the context is disposed, so abandoned mutations cannot be saved later.
/// Database atomicity covers Telemetry only; preliminary Device eligibility remains a separate read.
/// </remarks>
public sealed class TelemetryIngestionTransaction : ITelemetryIngestionTransaction
{
    private readonly TelemetryDbContext _context;
    private readonly ISimulationBatchRepository _receipts;
    private readonly IncidentLifecycleProcessor _incidents;
    private readonly TimeProvider _time;
    private readonly ILogger<TelemetryIngestionTransaction> _logger;
    private bool _attempted;

    /// <summary>Uses dependencies from the same ingestion scope without creating a separate pool or context.</summary>
    /// <param name="context">Exclusive scoped context; must have no existing transaction or tracked entities.</param>
    /// <param name="receipts">Repository registered against that same context.</param>
    /// <param name="incidents">Existing domain incident policy processor.</param>
    /// <param name="timeProvider">Server clock for post-lock observation and planned commit metadata.</param>
    /// <param name="logger">Receives sanitized diagnostics if cleanup fails after a confirmed result.</param>
    /// <exception cref="ArgumentNullException">A dependency is null.</exception>
    public TelemetryIngestionTransaction(TelemetryDbContext context, ISimulationBatchRepository receipts,
        IncidentLifecycleProcessor incidents, TimeProvider timeProvider, ILogger<TelemetryIngestionTransaction> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _receipts = receipts ?? throw new ArgumentNullException(nameof(receipts));
        _incidents = incidents ?? throw new ArgumentNullException(nameof(incidents));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">The validated batch is null.</exception>
    /// <exception cref="InvalidOperationException">This attempt was used already, or caller-owned tracking/transactions exist.</exception>
    /// <exception cref="TelemetryIngestionBusyException">The shared ingestion lock cannot be acquired within its bounded wait.</exception>
    public async Task<TelemetryIngestionResult> ExecuteAsync(TelemetryIngestionBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        cancellationToken.ThrowIfCancellationRequested();
        if (_attempted)
            throw new InvalidOperationException("Each ingestion transaction requires a fresh scope.");
        if (System.Transactions.Transaction.Current is not null
            || _context.Database.CurrentTransaction is not null
            || System.Transactions.TransactionsDatabaseFacadeExtensions.GetEnlistedTransaction(_context.Database) is not null
            || _context.ChangeTracker.Entries().Any())
            throw new InvalidOperationException("Ingestion requires an exclusive context without existing transactions or tracking.");

        // Rejected caller-owned work is untouched. From this point onward, failure invalidates
        // this context, even if connection acquisition or the initial transaction fails.
        _attempted = true;
        IDbContextTransaction? transaction = null;
        TelemetryIngestionResult? result = null;
        Exception? failure = null;
        bool confirmed = false;
        try
        {
            transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            try { await TelemetryIngestionLock.AcquireAsync(_context, cancellationToken); }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                throw new TelemetryIngestionBusyException();
            }

            // READ COMMITTED makes this query observe a duplicate committed while we waited.
            var receipt = await _receipts.FindByIdAsync(batch.BatchId, cancellationToken);
            if (receipt is not null)
            {
                if (!string.Equals(receipt.RequestHash, batch.RequestHash, StringComparison.Ordinal))
                    throw new TelemetryIngestionConflictException();
                result = new(receipt.BatchId, receipt.AffectedDeviceCount, receipt.CommittedAtUtc, true);
                confirmed = true; // This receipt was committed independently of our read-only transaction.
            }
            else
            {
                DateTimeOffset observedAt = _time.GetUtcNow();
                await StageObservationsAsync(batch, observedAt, cancellationToken);
                DateTimeOffset committedAt = _time.GetUtcNow();
                if (committedAt < observedAt)
                    throw new InvalidOperationException("Commit metadata cannot precede the observation time.");
                _receipts.Add(SimulationBatch.CreateCommitted(batch.BatchId, batch.RequestHash, batch.Scenario,
                    batch.RequestedAtUtc, committedAt, batch.DeviceIds.Count));
                // EF orders the receipt/ping/state/incident inserts using the existing local FKs.
                // Saving does not commit this explicit transaction; injected failures can still roll it back.
                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                confirmed = true;
                result = new(batch.BatchId, batch.DeviceIds.Count, committedAt, false);
            }
        }
        catch (Exception exception) { failure = exception; }

        // Disposal rolls back uncommitted writes and releases the transaction-scoped lock.
        // Preserve the original failure instead of replacing it with a later cleanup exception.
        if (transaction is not null)
        {
            try { await transaction.DisposeAsync(); }
            catch (Exception cleanup) { failure = RecordCleanupFailure(failure, cleanup, batch.BatchId); }
        }
        if (failure is not null)
        {
            try { await _context.DisposeAsync(); }
            catch (Exception cleanup) { failure = RecordCleanupFailure(failure, cleanup, batch.BatchId); }
            if (!confirmed) ExceptionDispatchInfo.Capture(failure).Throw();
            // Cleanup diagnostics cannot revoke an acknowledged commit or saved replay result.
        }
        return result!;
    }

    /// <summary>Stages domain transitions under the acquired transaction lock without saving independently.</summary>
    /// <param name="batch">Application-validated stable request content.</param>
    /// <param name="observedAt">One server observation time captured after lock acquisition for the entire batch.</param>
    /// <param name="token">Cancellation for reads and per-device processing.</param>
    /// <returns>Completion after pings, states, and incident changes have been staged in the same context.</returns>
    private async Task StageObservationsAsync(TelemetryIngestionBatch batch, DateTimeOffset observedAt, CancellationToken token)
    {
        Guid[] ids = batch.DeviceIds.ToArray();
        var states = await _context.DeviceTelemetryStates.Where(state => ids.Contains(state.DeviceId))
            .ToDictionaryAsync(state => state.DeviceId, token);
        var open = await _context.IncidentLogs.Where(incident => ids.Contains(incident.DeviceId) && incident.ResolvedAtUtc == null)
            .ToListAsync(token);
        foreach (Guid id in ids)
        {
            token.ThrowIfCancellationRequested();
            var ping = TelemetryScenarioFactory.Create(batch.Scenario, Guid.NewGuid(), id, batch.BatchId, observedAt);
            _context.TelemetryPings.Add(ping);
            if (states.TryGetValue(id, out var state)) state.ApplyPing(ping);
            else _context.DeviceTelemetryStates.Add(DeviceTelemetryState.Create(ping));
            var deviceIncidents = open.Where(incident => incident.DeviceId == id).ToList();
            var knownIds = deviceIncidents.Select(incident => incident.Id).ToHashSet();
            _incidents.Process(ping, deviceIncidents, Guid.NewGuid);
            // Existing resolutions remain tracked; only newly opened incidents need explicit Add.
            _context.IncidentLogs.AddRange(deviceIncidents.Where(incident => !knownIds.Contains(incident.Id)));
        }
    }

    /// <summary>Records sanitized cleanup diagnostics without changing a primary failure's type or stack.</summary>
    /// <param name="primary">Original operation failure, if any.</param>
    /// <param name="cleanup">Later disposal failure; its message is intentionally not logged.</param>
    /// <param name="batchId">Logical request identity for operational correlation.</param>
    /// <returns>The original failure, or the cleanup failure when no operation failed.</returns>
    private Exception RecordCleanupFailure(Exception? primary, Exception cleanup, Guid batchId)
    {
        try
        {
            _logger.LogWarning("Ingestion cleanup failed ({ExceptionType}) for batch {BatchId}; discard this scope.",
                cleanup.GetType().Name, batchId);
        }
        catch { /* A diagnostic sink cannot replace the primary failure or revoke confirmed success. */ }
        return primary ?? cleanup;
    }
}
