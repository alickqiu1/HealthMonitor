namespace HealthMonitor.Application.Telemetry;

/// <summary>Deletes one bounded batch of eligible Telemetry history under the shared ingestion/retention lock.</summary>
/// <remarks>
/// An implementation owns one transaction per call and deletes pings, resolved incidents, then unreferenced receipts
/// sequentially. The combined deletion count across all three tables cannot exceed MaximumRowsPerBatch.
/// Protect state-referenced pings and open incidents; nullable incident ping references may be cleared by the database.
/// A successful return confirms commit. An exception or cancellation does not establish whether commit occurred.
/// Reuse the scoped implementation only after successful cleanup; an admitted failure invalidates its context.
/// </remarks>
public interface ITelemetryRetentionStore
{
    /// <summary>Fixed maximum total deleted rows in one transaction, not a separate allowance for each table.</summary>
    const int MaximumRowsPerBatch = 1_000;

    /// <summary>Prunes one batch using strict cutoffs and reports eligibility observed under the acquired lock.</summary>
    /// <param name="pingObservedBeforeUtc">UTC exclusive observation-time cutoff; equality must be retained.</param>
    /// <param name="historyBeforeUtc">UTC exclusive incident-resolution/receipt-commit cutoff; equality must be retained.</param>
    /// <param name="cancellationToken">Cancellation of the complete transaction and lock acquisition.</param>
    /// <returns>
    /// Confirmed deleted counts and whether eligible rows remained at the final locked query's observation time.
    /// Backlog excludes protected latest-state pings, open incidents, and receipts still referenced by retained pings.
    /// It is not a guarantee about rows becoming eligible or arriving after the lock is released.
    /// </returns>
    /// <remarks>
    /// Receipt eligibility must see prior ping deletions in this transaction. Separate sequential commands avoid
    /// assuming sibling data-modifying CTEs observe each other's changes. Preserve at least seven days of receipts.
    /// The implementation rejects caller-owned transactions/tracking and uses the existing two-second shared lock.
    /// </remarks>
    /// <exception cref="OperationCanceledException">Cancellation was requested; the commit outcome may be uncertain.</exception>
    Task<TelemetryRetentionBatchResult> PruneBatchAsync(
        DateTimeOffset pingObservedBeforeUtc,
        DateTimeOffset historyBeforeUtc,
        CancellationToken cancellationToken = default);
}

/// <summary>Counts confirmed deletions from one committed retention transaction.</summary>
/// <param name="DeletedPings">Deleted ping rows; excludes any incident-reference updates caused by foreign keys.</param>
/// <param name="DeletedIncidents">Deleted resolved incident rows.</param>
/// <param name="DeletedReceipts">Deleted unreferenced simulation receipt rows.</param>
/// <param name="HasBacklog">Whether eligible rows remained at the final locked observation.</param>
public sealed record TelemetryRetentionBatchResult(
    int DeletedPings, int DeletedIncidents, int DeletedReceipts, bool HasBacklog);
