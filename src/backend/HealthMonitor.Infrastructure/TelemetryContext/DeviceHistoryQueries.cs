using System.Data;
using HealthMonitor.Application.IntegrationContracts;
using HealthMonitor.Application.Telemetry;
using HealthMonitor.Domain.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace HealthMonitor.Infrastructure.Data.TelemetryContext;

/// <summary>Projects history with count/page consistency under an explicitly read-only PostgreSQL transaction.</summary>
/// <remarks>Owns each transaction, never the context or pool. Failures require a fresh scope; caller transactions are rejected untouched.</remarks>
public sealed class DeviceHistoryQueries
    : IDeviceHistoryQueries
{
    private readonly TelemetryDbContext context;

    /// <summary>Uses the separately configured scoped context without opening a connection.</summary>
    /// <param name="context">Non-null caller-owned Telemetry context, used sequentially.</param>
    /// <exception cref="ArgumentNullException">Context is null.</exception>
    public DeviceHistoryQueries(TelemetryDbContext context) => this.context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public Task<HistoryPage<TelemetryHistoryItem>> GetTelemetryAsync(Guid deviceId, HistoryPaging paging,
        TelemetryHistoryRange range, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireIdentity(deviceId); ArgumentNullException.ThrowIfNull(paging); ArgumentNullException.ThrowIfNull(range);
        return ReadAsync(async token =>
        {
            // PostgreSQL stores microseconds, while a valid .NET endpoint may have residual 100 ns ticks.
            // Floor only parameters: a fractional lower bound is strict, and a fractional upper bound includes its floor.
            // This preserves literal [from,to) membership without rounding overflow at DateTimeOffset.MaxValue.
            var from = FloorMicrosecond(range.FromUtc);
            var to = FloorMicrosecond(range.ToUtc);
            bool includeFrom = from == range.FromUtc;
            bool includeTo = to != range.ToUtc;
            var filtered = context.TelemetryPings.AsNoTracking().Where(p => p.DeviceId == deviceId &&
                ((includeFrom && p.ObservedAtUtc >= from) || (!includeFrom && p.ObservedAtUtc > from)) &&
                ((includeTo && p.ObservedAtUtc <= to) || (!includeTo && p.ObservedAtUtc < to)));
            long count = await filtered.LongCountAsync(token);
            IQueryable<TelemetryPing> page;
            if (paging.Offset <= int.MaxValue)
                page = filtered.OrderByDescending(p => p.ObservedAtUtc).ThenByDescending(p => p.Id)
                    .Skip((int)paging.Offset).Take(paging.PageSize);
            else
                // LINQ Skip accepts Int32. Keep valid large pages without narrowing/overflow or changing the wire contract.
                // Mapped-entity SQL preserves EF enum conversions; values are parameters, never model-generated SQL.
                page = context.TelemetryPings.FromSqlInterpolated($"""
                    SELECT * FROM telemetry.telemetry_pings
                    WHERE device_id = {deviceId}
                        AND (({includeFrom} AND observed_at_utc >= {from}) OR ({!includeFrom} AND observed_at_utc > {from}))
                        AND (({includeTo} AND observed_at_utc <= {to}) OR ({!includeTo} AND observed_at_utc < {to}))
                    ORDER BY observed_at_utc DESC, id DESC LIMIT {paging.PageSize} OFFSET {paging.Offset}
                    """).AsNoTracking().OrderByDescending(p => p.ObservedAtUtc).ThenByDescending(p => p.Id);
            var items = await page.Select(p => new TelemetryHistoryItem(p.Id, p.DeviceId, p.ObservedAtUtc,
                p.Outcome, p.LatencyMs, p.CpuPercent)).ToArrayAsync(token);
            return new HistoryPage<TelemetryHistoryItem>(Array.AsReadOnly(items), paging, count);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<HistoryPage<IncidentHistoryItem>> GetIncidentsAsync(Guid deviceId, HistoryPaging paging,
        IncidentHistoryState state = IncidentHistoryState.All, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireIdentity(deviceId); ArgumentNullException.ThrowIfNull(paging);
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        return ReadAsync(async token =>
        {
            var filtered = context.IncidentLogs.AsNoTracking().Where(i => i.DeviceId == deviceId &&
                (state == IncidentHistoryState.All || (state == IncidentHistoryState.Open && i.ResolvedAtUtc == null) ||
                 (state == IncidentHistoryState.Resolved && i.ResolvedAtUtc != null)));
            long count = await filtered.LongCountAsync(token);
            IQueryable<IncidentLog> page;
            if (paging.Offset <= int.MaxValue)
                page = filtered.OrderByDescending(i => i.OpenedAtUtc).ThenByDescending(i => i.Id)
                    .Skip((int)paging.Offset).Take(paging.PageSize);
            else
                page = context.IncidentLogs.FromSqlInterpolated($"""
                    SELECT * FROM telemetry.incident_logs
                    WHERE device_id = {deviceId} AND ({state == IncidentHistoryState.All} OR
                        (resolved_at_utc IS NULL) = {state == IncidentHistoryState.Open})
                    ORDER BY opened_at_utc DESC, id DESC LIMIT {paging.PageSize} OFFSET {paging.Offset}
                    """).AsNoTracking().OrderByDescending(i => i.OpenedAtUtc).ThenByDescending(i => i.Id);
            var items = await page.Select(i => new IncidentHistoryItem(i.Id, i.DeviceId, i.Kind,
                i.OpenedAtUtc, i.ResolvedAtUtc, i.Summary)).ToArrayAsync(token);
            return new HistoryPage<IncidentHistoryItem>(Array.AsReadOnly(items), paging, count);
        }, cancellationToken);
    }

    /// <summary>Owns only this read's repeatable snapshot and preserves the primary failure through disposal.</summary>
    /// <typeparam name="T">Concrete projected history page.</typeparam>
    /// <param name="read">Sequential count and page using identical membership predicates.</param>
    /// <param name="token">Operation cancellation.</param>
    /// <returns>The page only after successful transaction completion and cleanup.</returns>
    /// <exception cref="InvalidOperationException">A caller-owned transaction exists.</exception>
    private async Task<T> ReadAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken token)
    {
        if (System.Transactions.Transaction.Current is not null || context.Database.CurrentTransaction is not null ||
            System.Transactions.TransactionsDatabaseFacadeExtensions.GetEnlistedTransaction(context.Database) is not null)
            throw new InvalidOperationException("History reads require a fresh scope without a caller transaction.");
        var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        T result;
        try
        {
            // MVCC stabilizes count/page even when ingestion or incident resolution commits between the statements.
            // Explicit read-only mode also prevents accidental future writes from broadening this adapter's privilege.
            await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", token);
            result = await read(token);
            await transaction.CommitAsync(token);
        }
        catch
        {
            try { await transaction.DisposeAsync(); }
            catch { /* Cleanup failure must not replace the original provider/cancellation failure. Discard the scope. */ }
            throw;
        }
        await transaction.DisposeAsync();
        return result;
    }

    /// <summary>Floors a validated zero-offset UTC endpoint for PostgreSQL parameter precision only.</summary>
    /// <param name="value">Original endpoint, retained unchanged in the Application request.</param>
    /// <returns>Whole-microsecond parameter; comparison inclusivity preserves the original endpoint's meaning.</returns>
    private static DateTimeOffset FloorMicrosecond(DateTimeOffset value) => value.AddTicks(-(value.UtcTicks % 10));
    /// <summary>Rejects an invalid internal identity before any transaction or database work.</summary>
    /// <param name="deviceId">Requested Telemetry identity.</param>
    /// <exception cref="ArgumentException">Identity is empty.</exception>
    private static void RequireIdentity(Guid deviceId)
    {
        if (deviceId == Guid.Empty) throw new ArgumentException("A nonempty device identity is required.", nameof(deviceId));
    }
}
