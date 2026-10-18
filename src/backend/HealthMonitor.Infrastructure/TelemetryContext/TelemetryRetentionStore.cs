using System.Data;
using System.Runtime.ExceptionServices;
using HealthMonitor.Application.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace HealthMonitor.Infrastructure.Data.TelemetryContext;

/// <summary>Prunes bounded Telemetry history using the same transaction-scoped lock as ingestion.</summary>
/// <remarks>
/// One exclusive scoped context may serve sequential successful batches. Rejected caller-owned work is untouched;
/// an admitted failure or failed cleanup invalidates the adapter and context. No automatic write retry occurs.
/// Counts describe acknowledged commits only; an unacknowledged commit can remain uncertain.
/// </remarks>
public sealed class TelemetryRetentionStore : ITelemetryRetentionStore
{
    private readonly TelemetryDbContext _context;
    private readonly ILogger<TelemetryRetentionStore> _logger;
    // Atomic ownership: Ready=0, InUse=1, Invalid=2. A rejected contender never resets another call's state.
    private int _state;

    /// <summary>Creates a scoped adapter without opening a database connection.</summary>
    /// <param name="context">Exclusive Telemetry context; never an ingestion context concurrently in use.</param>
    /// <param name="logger">Receives owned cleanup diagnostics without provider exception details.</param>
    /// <exception cref="ArgumentNullException">A dependency is null.</exception>
    public TelemetryRetentionStore(TelemetryDbContext context, ILogger<TelemetryRetentionStore> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">Cutoffs are not UTC or history is newer than the ping cutoff.</exception>
    /// <exception cref="InvalidOperationException">Context ownership, transactions, tracking, or adapter state is incompatible.</exception>
    public async Task<TelemetryRetentionBatchResult> PruneBatchAsync(DateTimeOffset pingObservedBeforeUtc,
        DateTimeOffset historyBeforeUtc, CancellationToken cancellationToken = default)
    {
        if (pingObservedBeforeUtc.Offset != TimeSpan.Zero || historyBeforeUtc.Offset != TimeSpan.Zero || historyBeforeUtc > pingObservedBeforeUtc)
            throw new ArgumentException("Retention cutoffs must be UTC with history no newer than the ping cutoff.");
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            throw new InvalidOperationException("Retention requires a ready, exclusively owned context.");

        bool admitted = false;
        bool reusable = true;
        Exception? failure = null;
        IDbContextTransaction? transaction = null;
        TelemetryRetentionBatchResult? result = null;
        try
        {
            if (System.Transactions.Transaction.Current is not null || _context.Database.CurrentTransaction is not null ||
                System.Transactions.TransactionsDatabaseFacadeExtensions.GetEnlistedTransaction(_context.Database) is not null ||
                _context.ChangeTracker.Entries().Any())
                throw new InvalidOperationException("Retention requires an exclusive context without existing transactions or tracking.");
            admitted = true;
            transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await TelemetryIngestionLock.AcquireAsync(_context, cancellationToken);
            int remaining = ITelemetryRetentionStore.MaximumRowsPerBatch;
            int pings = await _context.Database.ExecuteSqlInterpolatedAsync($"""
                WITH candidates AS (
                    SELECT p.id FROM telemetry.telemetry_pings p
                    WHERE p.observed_at_utc < {pingObservedBeforeUtc}
                      AND NOT EXISTS (SELECT 1 FROM telemetry.device_telemetry_state s WHERE s.last_ping_id = p.id)
                    ORDER BY p.observed_at_utc, p.id LIMIT {remaining}
                )
                DELETE FROM telemetry.telemetry_pings p USING candidates c WHERE p.id = c.id
                """, cancellationToken);
            remaining -= pings;
            int incidents = remaining == 0 ? 0 : await _context.Database.ExecuteSqlInterpolatedAsync($"""
                WITH candidates AS (
                    SELECT i.id FROM telemetry.incident_logs i
                    WHERE i.resolved_at_utc < {historyBeforeUtc}
                    ORDER BY i.resolved_at_utc, i.id LIMIT {remaining}
                )
                DELETE FROM telemetry.incident_logs i USING candidates c WHERE i.id = c.id
                """, cancellationToken);
            remaining -= incidents;

            // This is a separate command so receipt eligibility sees the ping deletions above in this transaction.
            int receipts = remaining == 0 ? 0 : await _context.Database.ExecuteSqlInterpolatedAsync($"""
                WITH candidates AS (
                    SELECT b.batch_id FROM telemetry.simulation_batches b
                    WHERE b.committed_at_utc < {historyBeforeUtc}
                      AND NOT EXISTS (SELECT 1 FROM telemetry.telemetry_pings p WHERE p.batch_id = b.batch_id)
                    ORDER BY b.committed_at_utc, b.batch_id LIMIT {remaining}
                )
                DELETE FROM telemetry.simulation_batches b USING candidates c WHERE b.batch_id = c.batch_id
                """, cancellationToken);
            bool backlog = await _context.Database.SqlQuery<bool>($"""
                SELECT (
                    EXISTS (SELECT 1 FROM telemetry.telemetry_pings p WHERE p.observed_at_utc < {pingObservedBeforeUtc}
                        AND NOT EXISTS (SELECT 1 FROM telemetry.device_telemetry_state s WHERE s.last_ping_id = p.id))
                    OR EXISTS (SELECT 1 FROM telemetry.incident_logs i WHERE i.resolved_at_utc < {historyBeforeUtc})
                    OR EXISTS (SELECT 1 FROM telemetry.simulation_batches b WHERE b.committed_at_utc < {historyBeforeUtc}
                        AND NOT EXISTS (SELECT 1 FROM telemetry.telemetry_pings p WHERE p.batch_id = b.batch_id))
                ) AS "Value"
                """).SingleAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            result = new(pings, incidents, receipts, backlog);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (admitted)
            {
                try { if (transaction is not null) await transaction.DisposeAsync(); }
                catch { reusable = false; CleanupDiagnostic(); }
                if (failure is not null || !reusable)
                {
                    // Invalidate before disposal so no contender can obtain a context whose rollback/cleanup is unresolved.
                    Volatile.Write(ref _state, 2);
                    try { await _context.DisposeAsync(); }
                    catch { CleanupDiagnostic(); }
                }
                else Volatile.Write(ref _state, 0);
            }
            else Volatile.Write(ref _state, 0);
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result!;
    }

    /// <summary>Reports cleanup trouble without masking an original failure or revoking an acknowledged commit.</summary>
    private void CleanupDiagnostic()
    {
        try { _logger.LogWarning("Retention context cleanup failed; the context is invalid and must not be reused."); }
        catch { /* Diagnostic sinks cannot replace transaction outcomes. */ }
    }
}
