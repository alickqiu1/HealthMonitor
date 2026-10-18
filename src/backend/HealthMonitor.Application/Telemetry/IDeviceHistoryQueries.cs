using HealthMonitor.Application.IntegrationContracts;

namespace HealthMonitor.Application.Telemetry;

/// <summary>Provides server-paged Telemetry history independently of catalog existence or retirement.</summary>
/// <remarks>
/// Each operation counts and pages in its own read-only repeatable-read transaction; no cross-module or later-page
/// snapshot is promised. Use sequentially in a fresh scope without an existing or ambient transaction.
/// Provider/cancellation failures propagate and require a fresh scope; there are no automatic retries.
/// </remarks>
public interface IDeviceHistoryQueries
{
    /// <summary>Reads one device's half-open telemetry window, newest observation then UUID first.</summary>
    /// <param name="deviceId">Nonempty identity whose catalog existence is established by the API.</param>
    /// <param name="paging">Non-null validated page coordinates.</param>
    /// <param name="range">Non-null validated UTC range of at most seven days.</param>
    /// <param name="cancellationToken">Cancellation throughout the count/page transaction.</param>
    /// <returns>Frozen page and true filtered total, including empty out-of-range pages.</returns>
    /// <exception cref="ArgumentException">Identity is empty.</exception>
    /// <exception cref="ArgumentNullException">Paging or range is null.</exception>
    /// <exception cref="InvalidOperationException">The scope already has a transaction.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    Task<HistoryPage<TelemetryHistoryItem>> GetTelemetryAsync(Guid deviceId, HistoryPaging paging,
        TelemetryHistoryRange range, CancellationToken cancellationToken = default);

    /// <summary>Reads incidents by opening time then UUID descending, filtered only by requested lifecycle state.</summary>
    /// <param name="deviceId">Nonempty identity whose catalog existence is established by the API.</param>
    /// <param name="paging">Non-null validated page coordinates.</param>
    /// <param name="state">Defined All, Open, or Resolved membership.</param>
    /// <param name="cancellationToken">Cancellation throughout the count/page transaction.</param>
    /// <returns>Frozen page and true state-filtered total; no date window is applied.</returns>
    /// <exception cref="ArgumentException">Identity is empty.</exception>
    /// <exception cref="ArgumentNullException">Paging is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">State is undefined.</exception>
    /// <exception cref="InvalidOperationException">The scope already has a transaction.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    Task<HistoryPage<IncidentHistoryItem>> GetIncidentsAsync(Guid deviceId, HistoryPaging paging,
        IncidentHistoryState state = IncidentHistoryState.All, CancellationToken cancellationToken = default);
}
