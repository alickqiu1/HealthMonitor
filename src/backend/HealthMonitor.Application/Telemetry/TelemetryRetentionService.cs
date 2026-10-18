namespace HealthMonitor.Application.Telemetry;

/// <summary>Bounds one retention pass using the Edition 1.5 retention windows and transaction limits.</summary>
/// <remarks>
/// Create a fresh scoped service for each pass. Individual store calls are atomic; the whole pass is not.
/// Earlier batches remain committed if a later one fails or cancellation is requested. No automatic retry occurs
/// inside the pass; the background worker will own scheduling and recovery using a fresh scope.
/// </remarks>
public sealed class TelemetryRetentionService
{
    /// <summary>Maximum committed batch calls per pass, bounding work without holding the ingestion lock for an hour's backlog.</summary>
    public const int MaximumBatchesPerPass = 10;
    private readonly ITelemetryRetentionStore _store;
    private readonly TimeProvider _time;
    private int _started;

    /// <summary>Gets the last validated committed-batch aggregate, or null if no batch return has been confirmed.</summary>
    /// <remarks>
    /// Inspect after pass completion or failure. This is confirmed progress only: a failing or invalid-result batch
    /// may also have committed and is not counted. HasBacklog describes the last confirmed batch's observation,
    /// not the current database after a later failure. This snapshot never establishes rollback or exact deletion totals.
    /// </remarks>
    public TelemetryRetentionPassResult? LastConfirmedProgress { get; private set; }

    /// <summary>Creates the scoped coordinator without accessing a database.</summary>
    /// <param name="store">Single-purpose bounded pruning port, called sequentially.</param>
    /// <param name="timeProvider">Server clock for one fixed pair of UTC cutoffs per pass.</param>
    /// <exception cref="ArgumentNullException">A dependency is null.</exception>
    public TelemetryRetentionService(ITelemetryRetentionStore store, TimeProvider timeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Runs at most ten bounded transactions, stopping when no eligible backlog or no deletion progress remains.</summary>
    /// <param name="cancellationToken">Cancellation before admission and between/within store calls.</param>
    /// <returns>Confirmed counts and backlog at the final successful batch's locked observation.</returns>
    /// <exception cref="InvalidOperationException">This instance already admitted a pass, or the store violated its count contract.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested; earlier or uncertain commits may remain.</exception>
    /// <remarks>
    /// Observation timestamps strictly older than 24 hours qualify; incident resolution and receipt commit timestamps
    /// strictly older than seven days qualify. Equality is retained. Fixed cutoffs keep a long pass consistent rather
    /// than continually admitting newly aged rows. Store exceptions propagate unchanged, preserving provider failure identity.
    /// </remarks>
    public async Task<TelemetryRetentionPassResult> RunPassAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("A retention service instance can admit only one pass.");

        var now = _time.GetUtcNow().ToUniversalTime();
        var pingCutoff = now - TimeSpan.FromHours(24);
        var historyCutoff = now - TimeSpan.FromDays(7);
        var progress = new TelemetryRetentionPassResult(0, 0, 0, 0, false);
        for (int batch = 0; batch < MaximumBatchesPerPass; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _store.PruneBatchAsync(pingCutoff, historyCutoff, cancellationToken);
            Validate(result);
            progress = new(progress.CompletedBatches + 1,
                progress.DeletedPings + result.DeletedPings,
                progress.DeletedIncidents + result.DeletedIncidents,
                progress.DeletedReceipts + result.DeletedReceipts, result.HasBacklog);
            LastConfirmedProgress = progress;

            // A confirmed commit must remain reportable even if shutdown arrived during its response.
            // Check cancellation on the next call boundary rather than losing this batch's confirmed counts.
            if (!result.HasBacklog || result.DeletedPings + result.DeletedIncidents + result.DeletedReceipts == 0)
                return progress;
        }
        return progress;
    }

    /// <summary>Rejects invalid adapter results before accepting them as confirmed progress.</summary>
    /// <param name="result">Reported committed batch counts.</param>
    /// <exception cref="InvalidOperationException">Counts are missing, negative, or exceed the combined fixed budget.</exception>
    private static void Validate(TelemetryRetentionBatchResult result)
    {
        // Bound each term before summing so even a malformed int.MaxValue result cannot overflow the safety check.
        int max = ITelemetryRetentionStore.MaximumRowsPerBatch;
        if (result is null || result.DeletedPings is < 0 or > ITelemetryRetentionStore.MaximumRowsPerBatch ||
            result.DeletedIncidents is < 0 or > ITelemetryRetentionStore.MaximumRowsPerBatch ||
            result.DeletedReceipts is < 0 or > ITelemetryRetentionStore.MaximumRowsPerBatch ||
            result.DeletedPings + result.DeletedIncidents + result.DeletedReceipts > max)
            throw new InvalidOperationException("Retention store returned invalid committed deletion counts.");
    }
}

/// <summary>Aggregates only confirmed, validated batch returns from a bounded pass.</summary>
/// <param name="CompletedBatches">Successful committed batch calls, including a zero-deletion inspection transaction.</param>
/// <param name="DeletedPings">Confirmed deleted ping rows.</param>
/// <param name="DeletedIncidents">Confirmed deleted resolved incident rows.</param>
/// <param name="DeletedReceipts">Confirmed deleted unreferenced receipt rows.</param>
/// <param name="HasBacklog">Eligible backlog at the final confirmed locked observation; zero-progress termination may leave it true.</param>
public sealed record TelemetryRetentionPassResult(
    int CompletedBatches, int DeletedPings, int DeletedIncidents, int DeletedReceipts, bool HasBacklog);
