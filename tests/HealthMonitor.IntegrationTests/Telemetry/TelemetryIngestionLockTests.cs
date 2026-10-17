using HealthMonitor.Application.Telemetry;
using HealthMonitor.Infrastructure.Data.TelemetryContext;
using HealthMonitor.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HealthMonitor.IntegrationTests.Telemetry;

/// <summary>Proves focused advisory-lock exclusion, release, cancellation and duplicate recheck with separate connections.</summary>
/// <remarks>These are bounded transaction guarantees for Step 3.4, not the expanded Step 3.5 concurrency matrix.</remarks>
[Collection(TelemetryIngestionDatabaseCollection.Name)]
public sealed class TelemetryIngestionLockTests
{
    // This fixed key is the reviewed ingestion/retention coordination contract, not a new test lock.
    private const long LockKey = 7142026001L;

    [Theory]
    [InlineData("commit")]
    [InlineData("rollback")]
    public Task AcquiredLock_ExcludesOthers_EndsWithTransaction(string completion) => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        await using var context = test.Context();
        await using var transaction = await context.Database.BeginTransactionAsync(test.Token);
        await TelemetryIngestionLock.AcquireAsync(context, test.Token);
        await using var probe = await test.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT pg_try_advisory_xact_lock({LockKey})", probe);
        Assert.False((bool)(await command.ExecuteScalarAsync(test.Token))!);
        if (completion == "rollback") await transaction.RollbackAsync(test.Token);
        else await transaction.CommitAsync(test.Token);
        Assert.True((bool)(await command.ExecuteScalarAsync(test.Token))!);
    });

    [Fact]
    public Task Contention_UsesTwoSecondTimeout_LeavesNoWrites() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        await using var connection = await test.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(test.Token);
        await using (var command = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({LockKey})", connection, transaction))
            await command.ExecuteNonQueryAsync(test.Token);
        var request = await test.RequestAsync();
        await using var context = test.Context();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TelemetryIngestionBusyException>(() => test.Start(test.Service(context), request));
        // This is a generous scheduling bound, not an assertion of millisecond timer accuracy.
        Assert.InRange(timer.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15));
        await transaction.RollbackAsync(test.Token);
        await using var read = test.Context();
        Assert.False(await read.SimulationBatches.AnyAsync(batch => batch.BatchId == request.BatchId, test.Token));
        Assert.False(await read.TelemetryPings.AnyAsync(ping => ping.BatchId == request.BatchId, test.Token));
    });

    [Fact]
    public Task CancellationWhileWaiting_PropagatesAndLeavesNoWrites() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        await using var connection = await test.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(test.Token);
        await using (var command = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({LockKey})", connection, transaction))
            await command.ExecuteNonQueryAsync(test.Token);
        var request = await test.RequestAsync();
        await using var context = test.Context();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(test.Token);
        Task<TelemetryIngestionResult> writer = test.Start(test.Service(context), request, cancel.Token);
        try
        {
            await using var waiting = new NpgsqlCommand("""
                SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted
                  AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                  AND classid::bigint = (@key >> 32) AND objid::bigint = (@key & 4294967295)
                """, connection, transaction);
            waiting.Parameters.AddWithValue("key", LockKey);
            while (Convert.ToInt64(await waiting.ExecuteScalarAsync(test.Token)) < 1)
                await Task.Delay(10, test.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer);
        }
        finally
        {
            cancel.Cancel();
            try { await writer; }
            catch { /* Observe the intentional cancellation before disposing its context. */ }
        }
        await transaction.RollbackAsync(test.Token);
        await using var read = test.Context();
        Assert.False(await read.SimulationBatches.AnyAsync(batch => batch.BatchId == request.BatchId, test.Token));
        Assert.False(await read.TelemetryPings.AnyAsync(ping => ping.BatchId == request.BatchId, test.Token));
    });

    [Fact]
    public Task DuplicateUnderLock_RecheckReturnsSingleCommittedBatch() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        var request = await test.RequestAsync("HighCpu");
        await using var blocker = await test.OpenConnectionAsync();
        await using var barrier = await blocker.BeginTransactionAsync(test.Token);
        await using (var command = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({LockKey})", blocker, barrier))
            await command.ExecuteNonQueryAsync(test.Token);
        await using var firstContext = test.Context();
        await using var secondContext = test.Context();
        Task<TelemetryIngestionResult> first = test.Start(test.Service(firstContext), request);
        Task<TelemetryIngestionResult> second = test.Start(test.Service(secondContext), request);
        bool released = false;
        try
        {
            // Observe both distinct sessions blocked at the real advisory lock. A task gate alone
            // cannot establish that both preliminary receipt lookups saw the missing receipt.
            await using (var waiting = new NpgsqlCommand("""
                SELECT count(DISTINCT pid) FROM pg_locks
                WHERE locktype = 'advisory' AND NOT granted
                  AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                  AND classid::bigint = (@key >> 32) AND objid::bigint = (@key & 4294967295)
                """, blocker, barrier))
            {
                waiting.Parameters.AddWithValue("key", LockKey);
                while (Convert.ToInt64(await waiting.ExecuteScalarAsync(test.Token)) < 2)
                    await Task.Delay(10, test.Token);
            }
            await barrier.RollbackAsync(test.Token);
            released = true;
            var results = await Task.WhenAll(first, second);
            Assert.Equal(new[] { false, true }, results.Select(result => result.Replayed).Order().ToArray());
            Assert.Equal(results[0].CommittedAtUtc, results[1].CommittedAtUtc);
            await using var read = test.Context();
            Assert.Equal(1, await read.SimulationBatches.CountAsync(batch => batch.BatchId == request.BatchId, test.Token));
            Assert.Equal(2, await read.TelemetryPings.CountAsync(ping => ping.BatchId == request.BatchId, test.Token));
            Assert.Equal(2, await read.IncidentLogs.CountAsync(incident => test.DeviceIds.Contains(incident.DeviceId), test.Token));
        }
        finally
        {
            // Release the barrier even when observation/assertions fail, then drain writers
            // before the surrounding await-using statements dispose their contexts.
            using var release = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (!released)
            {
                try { await barrier.RollbackAsync(release.Token); }
                catch { await blocker.CloseAsync(); }
            }
            try { await Task.WhenAll(first, second); }
            catch { /* The body propagates the assertion or worker failure; every writer is now terminal. */ }
        }
    });
}
