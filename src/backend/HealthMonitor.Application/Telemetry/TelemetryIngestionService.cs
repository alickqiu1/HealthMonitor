namespace HealthMonitor.Application.Telemetry;

/// <summary>Applies receipt-first replay and preliminary validation before atomic Telemetry persistence.</summary>
/// <remarks>
/// This service depends only on Application ports and existing validation policies. The transaction
/// implementation supplies the second receipt lookup under the database lock. Device retirement may
/// race this preliminary catalog read, as Edition 1.5 permits; no cross-module transaction is implied.
/// </remarks>
public sealed class TelemetryIngestionService : ITelemetryIngestionService
{
    private readonly ISimulationBatchRepository _receipts;
    private readonly IDeviceCatalog _catalog;
    private readonly ITelemetryIngestionTransaction _transaction;
    private readonly TimeProvider _timeProvider;
    private readonly TelemetryBatchIdValidator _batchIds;

    /// <summary>Creates orchestration with the scoped receipt/catalog boundaries and atomic writer.</summary>
    /// <param name="receipts">Receipt repository shared with the transaction implementation.</param>
    /// <param name="catalog">Telemetry's Device lookup port; invoked only for new requests.</param>
    /// <param name="transaction">Ingestion-specific atomic persistence boundary.</param>
    /// <param name="timeProvider">Server clock for request metadata and new-ID freshness validation.</param>
    /// <exception cref="ArgumentNullException">A dependency is null.</exception>
    public TelemetryIngestionService(
        ISimulationBatchRepository receipts,
        IDeviceCatalog catalog,
        ITelemetryIngestionTransaction transaction,
        TimeProvider timeProvider)
    {
        _receipts = receipts ?? throw new ArgumentNullException(nameof(receipts));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _batchIds = new TelemetryBatchIdValidator(timeProvider);
    }

    /// <inheritdoc />
    /// <exception cref="TelemetryIngestionConflictException">An existing receipt has a different canonical hash.</exception>
    /// <exception cref="TelemetryDeviceEligibilityException">A requested registration is absent or retired.</exception>
    /// <remarks>
    /// A preliminary miss followed by failed freshness or eligibility validation rejects this attempt,
    /// even if another caller commits meanwhile. Only attempts reaching the transaction perform the
    /// locked recheck. A later retry with the same BatchId can replay the now-committed receipt.
    /// Dependency failures propagate; this method never converts an outage to an empty catalog.
    /// </remarks>
    public async Task<TelemetryIngestionResult> IngestAsync(
        TelemetryIngestionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var requestedAtUtc = _timeProvider.GetUtcNow();
        ArgumentNullException.ThrowIfNull(request.DeviceIds);

        // Hashing and all later awaits must use the same collection snapshot. A caller may
        // mutate its original list while receipt or catalog IO is suspended.
        var snapshot = request with { DeviceIds = Array.AsReadOnly(request.DeviceIds.ToArray()) };
        string hash = TelemetryRequestCanonicalizer.ComputeHash(snapshot);
        var receipt = await _receipts.FindByIdAsync(snapshot.BatchId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (receipt is not null)
        {
            if (!string.Equals(receipt.RequestHash, hash, StringComparison.Ordinal))
                throw new TelemetryIngestionConflictException();
            return new TelemetryIngestionResult(receipt.BatchId, receipt.AffectedDeviceCount,
                receipt.CommittedAtUtc, Replayed: true);
        }

        // Historical replay deliberately bypasses freshness and current inventory eligibility.
        _batchIds.ValidateNewBatch(snapshot.BatchId);
        var registrations = await _catalog.GetByIdsAsync(snapshot.DeviceIds, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var activeIds = registrations.Where(device => !device.IsRetired)
            .Select(device => device.DeviceId).ToHashSet();
        if (snapshot.DeviceIds.Any(id => !activeIds.Contains(id)))
            throw new TelemetryDeviceEligibilityException();

        return await _transaction.ExecuteAsync(
            new TelemetryIngestionBatch(snapshot, hash, requestedAtUtc), cancellationToken);
    }
}
