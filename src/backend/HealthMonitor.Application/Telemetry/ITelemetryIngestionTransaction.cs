namespace HealthMonitor.Application.Telemetry;

/// <summary>Persists one validated ingestion batch through the Telemetry-owned atomic workflow.</summary>
/// <remarks>
/// The implementation owns one transaction, the shared ingestion lock, the receipt recheck,
/// and persistence of observations, state, incidents, and receipt using existing domain rules.
/// It must use the same scoped persistence context as the receipt repository. It does not
/// repeat Device eligibility or UUIDv7 freshness checks, which precede this boundary.
/// </remarks>
public interface ITelemetryIngestionTransaction
{
    /// <summary>Commits the batch, or replays a receipt committed while this attempt waited for the lock.</summary>
    /// <param name="batch">Immutable Application-validated inputs, including their canonical request hash.</param>
    /// <param name="cancellationToken">Cancellation for lock acquisition and transactional work.</param>
    /// <returns>Confirmed committed receipt information, with replay indicated when applicable.</returns>
    /// <exception cref="TelemetryIngestionConflictException">The locked receipt has different request content.</exception>
    /// <exception cref="OperationCanceledException">An operation observes cancellation; commit outcome may be uncertain.</exception>
    /// <remarks>
    /// Use a fresh DI scope for each attempt. Reject caller-owned transactions or tracking
    /// before admission; invalidate the attempt's context after an admitted failure so abandoned
    /// mutations cannot later be saved. A lost commit acknowledgement is not proof of rollback.
    /// Retry the same BatchId in a fresh scope to resolve its outcome through receipt-first replay.
    /// HTTP translation, automatic retries, and distributed Device transactions are outside this port.
    /// </remarks>
    Task<TelemetryIngestionResult> ExecuteAsync(
        TelemetryIngestionBatch batch,
        CancellationToken cancellationToken = default);
}
