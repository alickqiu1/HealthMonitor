using HealthMonitor.Application.Device;
using HealthMonitor.Application.IntegrationContracts;
using HealthMonitor.Application.Telemetry;

namespace HealthMonitor.Api.Fleet;

/// <summary>Validates drawer inputs and establishes catalog existence before querying independent Telemetry history.</summary>
/// <remarks>Known retired devices remain readable. Each call captures one evaluation instant, not cross-module snapshot isolation.</remarks>
internal sealed class DeviceHistoryReadService
{
    private readonly IDeviceCatalogQueries catalog;
    private readonly IDeviceHistoryQueries history;
    private readonly TimeProvider clock;

    /// <summary>Creates a scoped history composer without opening storage.</summary>
    /// <param name="catalog">Device-owned detail lookup.</param>
    /// <param name="history">Telemetry-owned server-page queries.</param>
    /// <param name="clock">Clock read once per admitted request.</param>
    /// <exception cref="ArgumentNullException">A dependency is null.</exception>
    public DeviceHistoryReadService(IDeviceCatalogQueries catalog, IDeviceHistoryQueries history, TimeProvider clock)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.history = history ?? throw new ArgumentNullException(nameof(history));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Reads a known device's telemetry page after resolving and validating optional range endpoints.</summary>
    /// <param name="deviceId">Explicit catalog identity; empty/unknown returns null.</param>
    /// <param name="page">One-based page, default one.</param>
    /// <param name="pageSize">One through 100, default 25.</param>
    /// <param name="fromUtc">Inclusive UTC start; defaults to effective end minus 24 hours.</param>
    /// <param name="toUtc">Exclusive UTC end; defaults to captured asOfUtc.</param>
    /// <param name="cancellationToken">Cancellation before validation and across both module reads.</param>
    /// <returns>Page/evaluation instant, or null for unknown inventory.</returns>
    /// <exception cref="ArgumentException">Paging or range is invalid, before any catalog read.</exception>
    /// <exception cref="InvalidOperationException">A port returns another device's facts or an invalid page.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    internal async Task<DeviceHistorySnapshot<TelemetryHistoryItem>?> ReadTelemetryAsync(Guid deviceId, int page = 1,
        int pageSize = 25, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var paging = new HistoryPaging(page, pageSize);
        var asOfUtc = clock.GetUtcNow();
        var range = TelemetryHistoryRange.ForRequest(asOfUtc, fromUtc, toUtc);
        if (!await KnownAsync(deviceId, cancellationToken)) return null;
        var result = await history.GetTelemetryAsync(deviceId, paging, range, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePage(result, paging, result.Items.Select(item => item.DeviceId), deviceId);
        return new(asOfUtc, result with { Items = Array.AsReadOnly(result.Items.ToArray()) });
    }

    /// <summary>Reads a known device's incident page without introducing a date-range filter.</summary>
    /// <param name="deviceId">Explicit catalog identity; empty/unknown returns null.</param>
    /// <param name="page">One-based page, default one.</param>
    /// <param name="pageSize">One through 100, default 25.</param>
    /// <param name="state">All, Open, or Resolved membership.</param>
    /// <param name="cancellationToken">Cancellation throughout the operation.</param>
    /// <returns>State-filtered page/evaluation instant, or null for unknown inventory.</returns>
    /// <exception cref="ArgumentException">Paging or state is invalid, before any catalog read.</exception>
    /// <exception cref="InvalidOperationException">A port returns another device's facts or an invalid page.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    internal async Task<DeviceHistorySnapshot<IncidentHistoryItem>?> ReadIncidentsAsync(Guid deviceId, int page = 1,
        int pageSize = 25, IncidentHistoryState state = IncidentHistoryState.All, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var paging = new HistoryPaging(page, pageSize);
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        var asOfUtc = clock.GetUtcNow();
        if (asOfUtc == default || asOfUtc.Offset != TimeSpan.Zero) throw new ArgumentException("A valid UTC clock is required.");
        if (!await KnownAsync(deviceId, cancellationToken)) return null;
        var result = await history.GetIncidentsAsync(deviceId, paging, state, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePage(result, paging, result.Items.Select(item => item.DeviceId), deviceId);
        return new(asOfUtc, result with { Items = Array.AsReadOnly(result.Items.ToArray()) });
    }

    /// <summary>Checks existence through Device only; retirement is deliberately not an exclusion criterion.</summary>
    /// <param name="id">Requested identity.</param>
    /// <param name="token">Cancellation before absent results.</param>
    /// <returns>True for known active or retired inventory.</returns>
    /// <exception cref="InvalidOperationException">Device returns a mismatched projection.</exception>
    private async Task<bool> KnownAsync(Guid id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (id == Guid.Empty) return false;
        var detail = await catalog.GetDetailAsync(id, token);
        token.ThrowIfCancellationRequested();
        if (detail is null) return false;
        if (detail.Registration is null || detail.Registration.DeviceId != id)
            throw new InvalidOperationException("Catalog history lookup returned another identity.");
        return true;
    }

    /// <summary>Rejects cross-device leakage or inconsistent envelope coordinates instead of silently reinterpreting a port failure.</summary>
    /// <typeparam name="T">Concrete history item.</typeparam>
    /// <param name="result">Port result.</param>
    /// <param name="requested">Validated requested coordinates.</param>
    /// <param name="ids">Device identities in the result.</param>
    /// <param name="id">Expected identity.</param>
    /// <exception cref="InvalidOperationException">Page metadata or membership is invalid.</exception>
    private static void ValidatePage<T>(HistoryPage<T> result, HistoryPaging requested, IEnumerable<Guid> ids, Guid id)
    {
        if (result.Paging.Page != requested.Page || result.Paging.PageSize != requested.PageSize ||
            result.TotalCount < 0 || result.Items.Count > requested.PageSize || result.Items.Count > result.TotalCount ||
            ids.Any(actual => actual != id))
            throw new InvalidOperationException("History returned inconsistent page coordinates or device membership.");
        long expected = Math.Min((long)requested.PageSize, Math.Max(0, result.TotalCount - requested.Offset));
        if (result.Items.Count != expected)
            throw new InvalidOperationException("History count and page membership are inconsistent.");
    }
}

/// <summary>One history page with the API's pre-read evaluation instant.</summary>
/// <typeparam name="T">Concrete immutable history projection.</typeparam>
/// <param name="AsOfUtc">Evaluation time, not a historical/cross-module snapshot guarantee.</param>
/// <param name="Page">Frozen page and its repeatable-read count.</param>
internal sealed record DeviceHistorySnapshot<T>(DateTimeOffset AsOfUtc, HistoryPage<T> Page);
