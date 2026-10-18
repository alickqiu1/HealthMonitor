using System.Data.Common;
using System.Text.Json;
using HealthMonitor.Application.Telemetry;
using HealthMonitor.Domain.Telemetry;
using HealthMonitor.Infrastructure.Data.TelemetryContext;
using HealthMonitor.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace HealthMonitor.IntegrationTests.Telemetry;

/// <summary>Verifies real PostgreSQL retention only on an empty, fixture-validated, exclusively owned test database.</summary>
/// <remarks>No Device inventory is touched. Every receipt is reserved before seeding; cleanup waits for all registered writers.</remarks>
[Collection(TelemetryIngestionDatabaseCollection.Name)]
public sealed class TelemetryRetentionStoreTests
{
    private const long LockKey = 7142026001L;

    /// <summary>Fails before seeding when unrelated Telemetry history exists, instead of allowing global pruning to adopt it.</summary>
    private static Task RunAsync(Func<TelemetryIngestionTestScenario, Task> body) => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        await test.RequireEmptyTelemetryAsync();
        await body(test);
    });

    /// <summary>Creates the real scoped adapter on the exact caller-owned test context.</summary>
    private static TelemetryRetentionStore Store(TelemetryDbContext context) => new(context, NullLogger<TelemetryRetentionStore>.Instance);
    /// <summary>Registers each real pruning attempt before returning it, including expected faults.</summary>
    private static Task<TelemetryRetentionBatchResult> Prune(TelemetryIngestionTestScenario test, TelemetryRetentionStore store,
        CancellationToken token = default) => test.TrackWriter(store.PruneBatchAsync(test.UtcNow.AddHours(-24), test.UtcNow.AddDays(-7),
            token == default ? test.Token : token));

    /// <summary>Seeds only reserved receipts/pings in chunks of at most 250, avoiding an unbounded tracked graph or command batch.</summary>
    /// <param name="test">Scenario owning all device and receipt identities.</param>
    /// <param name="count">Bounded test inventory size, up to 10,001.</param>
    /// <param name="observed">UTC ping time.</param>
    /// <returns>The generated ping snapshots, with their owned receipt identities.</returns>
    private static async Task<TelemetryPing[]> SeedAsync(TelemetryIngestionTestScenario test, int count,
        DateTimeOffset observed)
    {
        Assert.InRange(count, 1, 10001);
        var batches = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        await test.ReserveBatchIdsAsync(batches);
        var pings = batches.Select(batch => TelemetryPing.CreateSuccess(Guid.NewGuid(), test.DeviceIds[0], batch, observed, 40, 30m)).ToArray();
        await using var context = test.Context();
        for (int offset = 0; offset < count; offset += 250)
        {
            foreach (var ping in pings.Skip(offset).Take(250))
            {
                var receiptTime = observed;
                context.SimulationBatches.Add(SimulationBatch.CreateCommitted(ping.BatchId, new string('a', 64), "Healthy", receiptTime, receiptTime, 1));
                context.TelemetryPings.Add(ping);
            }
            await context.SaveChangesAsync(test.Token);
            context.ChangeTracker.Clear();
        }
        return pings;
    }

    /// <summary>Captures all owned table values, including nullable incident links, for exact rollback comparisons.</summary>
    private static async Task<string> SnapshotAsync(TelemetryIngestionTestScenario test)
    {
        await using var read = test.Context();
        return JsonSerializer.Serialize(new
        {
            Pings = await read.TelemetryPings.AsNoTracking().OrderBy(p => p.Id).ToArrayAsync(test.Token),
            Incidents = await read.IncidentLogs.AsNoTracking().OrderBy(i => i.Id).ToArrayAsync(test.Token),
            States = await read.DeviceTelemetryStates.AsNoTracking().OrderBy(s => s.DeviceId).ToArrayAsync(test.Token),
            Receipts = await read.SimulationBatches.AsNoTracking().OrderBy(b => b.BatchId).ToArrayAsync(test.Token)
        });
    }

    /// <summary>Seeds old opening/resolution references and a protected latest ping so rollback must restore FK updates too.</summary>
    private static async Task SeedRollbackAsync(TelemetryIngestionTestScenario test)
    {
        var old = (await SeedAsync(test, 1, test.UtcNow.AddDays(-10)))[0];
        var latest = (await SeedAsync(test, 1, test.UtcNow))[0];
        await using var context = test.Context();
        context.DeviceTelemetryStates.Add(DeviceTelemetryState.Create(latest));
        context.IncidentLogs.Add(IncidentLog.Open(Guid.NewGuid(), test.DeviceIds[0], IncidentKind.Cpu, old.ObservedAtUtc, old.Id, "Preserve open history"));
        var resolved = IncidentLog.Open(Guid.NewGuid(), test.DeviceIds[0], IncidentKind.Latency, old.ObservedAtUtc, old.Id, "Resolved old history");
        resolved.Resolve(test.UtcNow.AddDays(-9), old.Id);
        context.IncidentLogs.Add(resolved);
        await context.SaveChangesAsync(test.Token);
    }

    /// <summary>Injects only after a DELETE executed, covering raw non-query SQL rather than the unrelated SaveChanges path.</summary>
    private sealed class DeleteProbe : DbCommandInterceptor
    {
        /// <summary>Number of DELETE commands acknowledged before fault injection.</summary>
        internal int Deletes { get; private set; }
        /// <summary>Test-owned failure/cancellation/gate after the server executed a command.</summary>
        internal Func<int, CancellationToken, Task>? AfterDelete { get; set; }
        /// <inheritdoc />
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("DELETE FROM telemetry.", StringComparison.Ordinal))
            {
                Deletes++;
                if (AfterDelete is { } action) await action(Deletes, cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public Task EmptyBatch_NoChanges() => RunAsync(async test =>
    {
        await using var context = test.Context();
        Assert.Equal(new TelemetryRetentionBatchResult(0, 0, 0, false), await Prune(test, Store(context)));
        Assert.Empty(context.ChangeTracker.Entries());
        // The safety precondition must refuse existing data without deleting/adopting it, even when it is our sentinel.
        await SeedAsync(test, 1, test.UtcNow);
        var snapshot = await SnapshotAsync(test);
        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => test.RequireEmptyTelemetryAsync());
        Assert.Equal(snapshot, await SnapshotAsync(test));
    });

    [Fact]
    public Task CutoffEqualityRetained_OneMicrosecondOlderDeleted() => RunAsync(async test =>
    {
        var pingCutoff = test.UtcNow.AddHours(-24);
        var history = test.UtcNow.AddDays(-7);
        var equal = (await SeedAsync(test, 1, pingCutoff))[0];
        var older = (await SeedAsync(test, 1, pingCutoff.AddTicks(-10)))[0];
        var openings = await SeedAsync(test, 2, history.AddDays(-1));
        var orphanIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        await test.ReserveBatchIdsAsync(orphanIds);
        // Incident timestamps independently test equality versus one PostgreSQL microsecond before the resolution cutoff.
        await using (var seed = test.Context())
        {
            var kept = IncidentLog.Open(Guid.NewGuid(), test.DeviceIds[0], IncidentKind.Latency, history.AddDays(-1), openings[0].Id, "Boundary");
            kept.Resolve(history, openings[0].Id);
            var removed = IncidentLog.Open(Guid.NewGuid(), test.DeviceIds[0], IncidentKind.Cpu, history.AddDays(-1), openings[1].Id, "Older");
            removed.Resolve(history.AddTicks(-10), openings[1].Id);
            seed.SimulationBatches.Add(SimulationBatch.CreateCommitted(orphanIds[0], new string('a', 64), "Healthy", history, history, 1));
            var before = history.AddTicks(-10);
            seed.SimulationBatches.Add(SimulationBatch.CreateCommitted(orphanIds[1], new string('a', 64), "Healthy", before, before, 1));
            seed.IncidentLogs.AddRange(kept, removed);
            await seed.SaveChangesAsync(test.Token);
        }
        await using var context = test.Context();
        Assert.Equal(new TelemetryRetentionBatchResult(3, 1, 3, false), await Prune(test, Store(context)));
        Assert.Equal(equal.Id, (await context.TelemetryPings.AsNoTracking().SingleAsync(test.Token)).Id);
        Assert.Equal(history, (await context.IncidentLogs.AsNoTracking().SingleAsync(test.Token)).ResolvedAtUtc);
        var retainedReceipts = await context.SimulationBatches.AsNoTracking().Select(batch => batch.BatchId).ToArrayAsync(test.Token);
        Assert.Equal(new[] { equal.BatchId, older.BatchId, orphanIds[0] }.Order(), retainedReceipts.Order());
    });

    [Fact]
    public Task LatestStateAndOpenIncidentPreserved_PingReferencesSetNull() => RunAsync(async test =>
    {
        var opening = (await SeedAsync(test, 1, test.UtcNow.AddDays(-15)))[0];
        var latest = (await SeedAsync(test, 1, test.UtcNow.AddDays(-14)))[0];
        await using (var seed = test.Context())
        {
            seed.DeviceTelemetryStates.Add(DeviceTelemetryState.Create(latest));
            seed.IncidentLogs.Add(IncidentLog.Open(Guid.NewGuid(), test.DeviceIds[0], IncidentKind.Cpu, opening.ObservedAtUtc, opening.Id, "Retained metadata"));
            var recentSeed = IncidentLog.Open(Guid.NewGuid(), test.DeviceIds[0], IncidentKind.Latency,
                opening.ObservedAtUtc, opening.Id, "Recently resolved metadata");
            recentSeed.Resolve(test.UtcNow.AddDays(-2), opening.Id);
            seed.IncidentLogs.Add(recentSeed);
            await seed.SaveChangesAsync(test.Token);
        }
        await using var context = test.Context();
        Assert.Equal(new TelemetryRetentionBatchResult(1, 0, 1, false), await Prune(test, Store(context)));
        var incident = await context.IncidentLogs.AsNoTracking().SingleAsync(row => row.Kind == IncidentKind.Cpu, test.Token);
        Assert.Null(incident.OpenedByPingId);
        Assert.Null(incident.ResolvedAtUtc);
        Assert.Equal(opening.ObservedAtUtc, incident.OpenedAtUtc);
        Assert.Equal("Retained metadata", incident.Summary);
        Assert.Equal(IncidentKind.Cpu, incident.Kind);
        var recent = await context.IncidentLogs.AsNoTracking().SingleAsync(row => row.Kind == IncidentKind.Latency, test.Token);
        Assert.Null(recent.OpenedByPingId);
        Assert.Null(recent.ResolvedByPingId);
        Assert.Equal(test.UtcNow.AddDays(-2), recent.ResolvedAtUtc);
        Assert.Equal(opening.ObservedAtUtc, recent.OpenedAtUtc);
        Assert.Equal("Recently resolved metadata", recent.Summary);
        Assert.Equal(latest.Id, (await context.DeviceTelemetryStates.AsNoTracking().SingleAsync(test.Token)).LastPingId);
        Assert.Equal(latest.Id, (await context.TelemetryPings.AsNoTracking().SingleAsync(test.Token)).Id);
    });

    [Fact]
    public Task RetainedPingProtectsExpiredReceipt() => RunAsync(async test =>
    {
        var ping = (await SeedAsync(test, 1, test.UtcNow.AddDays(-8)))[0];
        await using (var seed = test.Context())
        {
            seed.DeviceTelemetryStates.Add(DeviceTelemetryState.Create(ping));
            await seed.SaveChangesAsync(test.Token);
        }
        await using var context = test.Context();
        Assert.Equal(new TelemetryRetentionBatchResult(0, 0, 0, false), await Prune(test, Store(context)));
        Assert.Equal(ping.BatchId, (await context.SimulationBatches.AsNoTracking().SingleAsync(test.Token)).BatchId);
    });

    [Fact]
    public Task CombinedBudgetAcrossTables_StopsAt1000() => RunAsync(async test =>
    {
        var pings = await SeedAsync(test, 500, test.UtcNow.AddDays(-10));
        await using (var seed = test.Context())
        {
            foreach (var ping in pings.Take(400))
            {
                var incident = IncidentLog.Open(Guid.NewGuid(), test.DeviceIds[0], IncidentKind.Latency, ping.ObservedAtUtc, ping.Id, "Old resolved");
                incident.Resolve(test.UtcNow.AddDays(-9), ping.Id);
                seed.IncidentLogs.Add(incident);
            }
            await seed.SaveChangesAsync(test.Token);
        }
        await using var context = test.Context();
        Assert.Equal(new TelemetryRetentionBatchResult(500, 400, 100, true), await Prune(test, Store(context)));
        Assert.Empty(await context.TelemetryPings.ToArrayAsync(test.Token));
        Assert.Empty(await context.IncidentLogs.ToArrayAsync(test.Token));
        Assert.Equal(400, await context.SimulationBatches.CountAsync(test.Token));
    });

    [Fact]
    public Task TenBatchPass_ReportsRemainingBacklog() => RunAsync(async test =>
    {
        await SeedAsync(test, 10001, test.UtcNow.AddDays(-2));
        await using var context = test.Context();
        var service = new TelemetryRetentionService(Store(context), test);
        var result = await test.TrackWriter(service.RunPassAsync(test.Token));
        Assert.Equal(new TelemetryRetentionPassResult(10, 10000, 0, 0, true), result);
        Assert.Equal(1, await context.TelemetryPings.CountAsync(test.Token));
        Assert.Equal(10001, await context.SimulationBatches.CountAsync(test.Token));
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Empty(context.ChangeTracker.Entries());
    });

    [Fact]
    public Task ExecutedDeletionFailure_RollsBackAllStages() => RunAsync(async test =>
    {
        await SeedRollbackAsync(test);
        string before = await SnapshotAsync(test);
        var error = new InvalidOperationException("After executed third deletion");
        var probe = new DeleteProbe { AfterDelete = (count, _) => count == 3 ? throw error : Task.CompletedTask };
        await using var context = test.Context(probe);
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => Prune(test, Store(context))));
        Assert.Equal(3, probe.Deletes);
        Assert.Equal(before, await SnapshotAsync(test));
    });

    [Fact]
    public Task CancelledDeletion_RollsBack() => RunAsync(async test =>
    {
        await SeedRollbackAsync(test);
        string before = await SnapshotAsync(test);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(test.Token);
        var probe = new DeleteProbe { AfterDelete = (_, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; } };
        await using var context = test.Context(probe);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Prune(test, Store(context), cancellation.Token));
        Assert.Equal(1, probe.Deletes);
        Assert.Equal(before, await SnapshotAsync(test));
    });

    [Fact]
    public Task IngestionLockContention_PreventsRetentionAndReleases() => RunAsync(async test =>
    {
        await SeedAsync(test, 1, test.UtcNow.AddDays(-2));
        string before = await SnapshotAsync(test);
        await using (var blocker = test.Context())
        {
            await using var transaction = await blocker.Database.BeginTransactionAsync(test.Token);
            await TelemetryIngestionLock.AcquireAsync(blocker, test.Token);
            await using var context = test.Context();
            var error = await Assert.ThrowsAsync<PostgresException>(() => Prune(test, Store(context)));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
            Assert.Equal(before, await SnapshotAsync(test));
            await transaction.RollbackAsync(test.Token);
        }
        await using var fresh = test.Context();
        Assert.Equal(1, (await Prune(test, Store(fresh))).DeletedPings);
    });

    [Fact]
    public Task WaitingIngestion_ProceedsAfterRetentionCommit() => RunAsync(async test =>
    {
        await SeedAsync(test, 1, test.UtcNow.AddDays(-2));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new DeleteProbe { AfterDelete = async (count, token) =>
        {
            if (count == 1) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
        } };
        var retentionContext = test.Context(probe);
        var ingestionContext = test.Context();
        Task<TelemetryRetentionBatchResult>? retention = null;
        Task<TelemetryIngestionResult>? ingestion = null;
        try
        {
            var request = await test.RequestAsync();
            await ingestionContext.Database.OpenConnectionAsync(test.Token);
            int pid = ((NpgsqlConnection)ingestionContext.Database.GetDbConnection()).ProcessID;
            var retentionStore = Store(retentionContext);
            retention = Prune(test, retentionStore);
            await Task.WhenAny(entered.Task, retention).WaitAsync(test.Token);
            if (!entered.Task.IsCompleted) await retention;
            await entered.Task.WaitAsync(test.Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Prune(test, retentionStore));
            ingestion = test.Start(test.Service(ingestionContext), request);
            await using var observer = await test.OpenConnectionAsync();
            await ObserveWaiterAsync(observer, pid, ingestion, test.Token);
            release.TrySetResult();
            Assert.Equal(1, (await retention).DeletedPings);
            Assert.False((await ingestion).Replayed);
            await using var read = test.Context();
            Assert.Equal(2, await read.TelemetryPings.CountAsync(test.Token));
            Assert.Equal(2, await read.DeviceTelemetryStates.CountAsync(test.Token));
        }
        finally
        {
            release.TrySetResult();
            await TelemetryIngestionTestScenario.DrainWritersAsync(new Task?[] { retention, ingestion }.OfType<Task>().ToArray());
            await retentionContext.DisposeAsync();
            await ingestionContext.DisposeAsync();
        }
    });

    [Fact]
    public Task RepeatedBatch_ReusesCleanContext() => RunAsync(async test =>
    {
        await SeedAsync(test, 1001, test.UtcNow.AddDays(-2));
        await using var context = test.Context();
        var store = Store(context);
        Assert.Equal(new TelemetryRetentionBatchResult(1000, 0, 0, true), await Prune(test, store));
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Empty(context.ChangeTracker.Entries());
        await using (var callerTransaction = await context.Database.BeginTransactionAsync(test.Token))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Prune(test, store));
            Assert.Same(callerTransaction, context.Database.CurrentTransaction);
            await callerTransaction.RollbackAsync(test.Token);
        }
        Assert.Equal(new TelemetryRetentionBatchResult(1, 0, 0, false), await Prune(test, store));
        Assert.Equal(new TelemetryRetentionBatchResult(0, 0, 0, false), await Prune(test, store));
    });

    [Fact]
    public Task FailedBatch_InvalidatesContext() => RunAsync(async test =>
    {
        await SeedAsync(test, 1, test.UtcNow.AddDays(-2));
        string before = await SnapshotAsync(test);
        var probe = new DeleteProbe { AfterDelete = (_, _) => throw new InvalidOperationException("Injected") };
        await using var context = test.Context(probe);
        var store = Store(context);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Prune(test, store));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Prune(test, store));
        Assert.Throws<ObjectDisposedException>(() => context.ChangeTracker.Entries().ToArray());
        Assert.Equal(before, await SnapshotAsync(test));

        var original = new InvalidOperationException("Original deletion failure");
        var failingCleanupProbe = new DeleteProbe { AfterDelete = (_, _) => throw original };
        await using (var failingCleanupContext = test.CleanupFailureContext(failingCleanupProbe))
        {
            Assert.Same(original, await Assert.ThrowsAsync<InvalidOperationException>(() => Prune(test, Store(failingCleanupContext))));
        }
        Assert.Equal(before, await SnapshotAsync(test));

        // Acknowledged commit survives cleanup failure, but its invalidated context must not serve another batch.
        await using var cleanupContext = test.CleanupFailureContext();
        var cleanupStore = Store(cleanupContext);
        Assert.Equal(1, (await Prune(test, cleanupStore)).DeletedPings);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Prune(test, cleanupStore));
        await using var read = test.Context();
        Assert.Empty(await read.TelemetryPings.ToArrayAsync(test.Token));
    });

    /// <summary>Observes the exact ingestion backend waiting on the shared key, failing early if it completes unexpectedly.</summary>
    private static async Task ObserveWaiterAsync(NpgsqlConnection observer, int pid, Task writer, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND NOT granted
              AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
              AND pid = @pid AND classid::bigint = (@key >> 32) AND objid::bigint = (@key & 4294967295))
            """, observer);
        command.Parameters.AddWithValue("pid", pid);
        command.Parameters.AddWithValue("key", LockKey);
        while (!(bool)(await command.ExecuteScalarAsync(token))!)
        {
            if (writer.IsCompleted) { await writer; throw new InvalidOperationException("Ingestion finished before its lock wait was observed."); }
            await Task.Delay(10, token);
        }
    }
}
