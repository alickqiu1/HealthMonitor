using HealthMonitor.Application.Telemetry;
using HealthMonitor.Domain.Telemetry;
using HealthMonitor.Infrastructure.Data.TelemetryContext;
using HealthMonitor.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace HealthMonitor.IntegrationTests.Telemetry;

/// <summary>Verifies competing ingestion attempts using distinct contexts, PostgreSQL sessions, and two independent pools.</summary>
/// <remarks>
/// These tests run within one serialized test process, not multiple operating-system processes.
/// The database lock supplies exclusion across independently pooled connections. All test gates
/// release and writers terminate before context/source disposal or scenario-owned row cleanup.
/// Production locking, timeouts and transaction code are unchanged.
/// </remarks>
[Collection(TelemetryIngestionDatabaseCollection.Name)]
public sealed class TelemetryIngestionConcurrencyTests
{
    private const long LockKey = 7142026001L;

    /// <summary>Captures expected conflict/failure outcomes while allowing every competing task to be observed.</summary>
    private sealed record Outcome(TelemetryIngestionResult? Result, Exception? Error);

    /// <summary>Holds executed, uncommitted writes until the successor is observed waiting, then injects failure.</summary>
    private sealed class PausedFailure : SaveChangesInterceptor
    {
        internal TaskCompletionSource Saved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception Failure { get; } = new InvalidOperationException("Injected failure after contended writes.");
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            Saved.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            throw Failure;
        }
    }

    [Fact]
    public Task IdenticalConcurrentRequests_CommitOnceAcrossSources() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        var request = await test.RequestAsync("HighCpu");
        await using var independent = await test.IndependentSourceAsync();
        var contexts = Enumerable.Range(0, 4).Select(index => index % 2 == 0 ? test.Context() : test.Context(independent)).ToArray();
        var writers = new List<Task<TelemetryIngestionResult>>();
        await using var blocker = await test.OpenConnectionAsync();
        await using var barrier = await blocker.BeginTransactionAsync(test.Token);
        bool released = false;
        try
        {
            int[] pids = await OpenSessionsAsync(contexts, test.Token);
            await LockAsync(blocker, barrier, test.Token);
            writers.AddRange(contexts.Select(context => test.Start(test.Service(context), request)));
            await ObserveWaitersAsync(blocker, barrier, pids, writers, test.Token);
            await barrier.RollbackAsync(test.Token);
            released = true;
            var results = await Task.WhenAll(writers);
            Assert.Equal(1, results.Count(result => !result.Replayed));
            Assert.Equal(3, results.Count(result => result.Replayed));
            Assert.Single(results.Select(result => (result.BatchId, result.AffectedDeviceCount, result.CommittedAtUtc)).Distinct());
            await using var read = test.Context();
            Assert.Equal(1, await read.SimulationBatches.CountAsync(batch => batch.BatchId == request.BatchId, test.Token));
            Assert.Equal(2, await read.TelemetryPings.CountAsync(ping => ping.BatchId == request.BatchId, test.Token));
            Assert.Equal(2, await read.IncidentLogs.CountAsync(incident => test.DeviceIds.Contains(incident.DeviceId) && incident.ResolvedAtUtc == null, test.Token));
            await AssertFinalStateAsync(test, read, new[] { request.BatchId }, "HighCpu");
        }
        finally { await ReleaseDrainDisposeAsync(blocker, barrier, released, writers, contexts); }
    });

    [Fact]
    public Task ConflictingConcurrentRequests_CommitOneCanonicalPayload() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        var request = await test.RequestAsync("Healthy");
        await using var independent = await test.IndependentSourceAsync();
        var contexts = new[] { test.Context(), test.Context(independent) };
        var writers = new List<Task<TelemetryIngestionResult>>();
        await using var blocker = await test.OpenConnectionAsync();
        await using var barrier = await blocker.BeginTransactionAsync(test.Token);
        bool released = false;
        try
        {
            int[] pids = await OpenSessionsAsync(contexts, test.Token);
            await LockAsync(blocker, barrier, test.Token);
            writers.Add(test.Start(test.Service(contexts[0]), request));
            writers.Add(test.Start(test.Service(contexts[1]), request with { Scenario = "HighCpu" }));
            await ObserveWaitersAsync(blocker, barrier, pids, writers, test.Token);
            await barrier.RollbackAsync(test.Token);
            released = true;
            var outcomes = await Task.WhenAll(writers.Select(ObserveOutcomeAsync));
            Assert.Single(outcomes, outcome => outcome.Result is { Replayed: false });
            Assert.IsType<TelemetryIngestionConflictException>(Assert.Single(outcomes, outcome => outcome.Error is not null).Error);
            await using var read = test.Context();
            var receipt = await read.SimulationBatches.SingleAsync(batch => batch.BatchId == request.BatchId, test.Token);
            Assert.Contains(receipt.Scenario, new[] { "Healthy", "HighCpu" });
            Assert.Equal(TelemetryRequestCanonicalizer.ComputeHash(request with { Scenario = receipt.Scenario }), receipt.RequestHash);
            var pings = await read.TelemetryPings.Where(ping => ping.BatchId == request.BatchId).ToArrayAsync(test.Token);
            Assert.Equal(2, pings.Length);
            Assert.All(pings, ping => Assert.Equal(receipt.Scenario == "Healthy" ? 30m : 95m, ping.CpuPercent));
            Assert.Equal(receipt.Scenario == "Healthy" ? 0 : 2,
                await read.IncidentLogs.CountAsync(incident => test.DeviceIds.Contains(incident.DeviceId), test.Token));
            await AssertFinalStateAsync(test, read, new[] { request.BatchId }, receipt.Scenario);
        }
        finally { await ReleaseDrainDisposeAsync(blocker, barrier, released, writers, contexts); }
    });

    [Fact]
    public Task DistinctConcurrentBatches_PreserveStateAndIncidentUniqueness() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        var requests = new List<TelemetryIngestionRequest>();
        for (int index = 0; index < 4; index++) requests.Add(await test.RequestAsync("HighCpu"));
        Guid[] batches = requests.Select(request => request.BatchId).ToArray();
        await using var independent = await test.IndependentSourceAsync();
        var contexts = Enumerable.Range(0, 4).Select(index => index % 2 == 0 ? test.Context() : test.Context(independent)).ToArray();
        var writers = new List<Task<TelemetryIngestionResult>>();
        await using var blocker = await test.OpenConnectionAsync();
        await using var barrier = await blocker.BeginTransactionAsync(test.Token);
        bool released = false;
        try
        {
            int[] pids = await OpenSessionsAsync(contexts, test.Token);
            await LockAsync(blocker, barrier, test.Token);
            for (int index = 0; index < 4; index++) writers.Add(test.Start(test.Service(contexts[index]), requests[index]));
            await ObserveWaitersAsync(blocker, barrier, pids, writers, test.Token);
            await barrier.RollbackAsync(test.Token);
            released = true;
            Assert.All(await Task.WhenAll(writers), result => Assert.False(result.Replayed));
            await using var read = test.Context();
            Assert.Equal(4, await read.SimulationBatches.CountAsync(batch => batches.Contains(batch.BatchId), test.Token));
            Assert.Equal(8, await read.TelemetryPings.CountAsync(ping => batches.Contains(ping.BatchId), test.Token));
            var incidents = await read.IncidentLogs.Where(incident => test.DeviceIds.Contains(incident.DeviceId)).ToArrayAsync(test.Token);
            Assert.Equal(2, incidents.Length);
            Assert.All(incidents, incident => { Assert.Equal(IncidentKind.Cpu, incident.Kind); Assert.Null(incident.ResolvedAtUtc); });
            Assert.Equal(2, incidents.Select(incident => incident.DeviceId).Distinct().Count());
            await AssertFinalStateAsync(test, read, batches, "HighCpu");
        }
        finally { await ReleaseDrainDisposeAsync(blocker, barrier, released, writers, contexts); }
    });

    [Fact]
    public Task FailedWriter_ReleasesLockForWaitingSuccessor() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        var failedRequest = await test.RequestAsync("HighCpu");
        var successor = await test.RequestAsync("Healthy");
        var injection = new PausedFailure();
        await using var independent = await test.IndependentSourceAsync();
        var contexts = new[] { test.Context(injection), test.Context(independent) };
        var writers = new List<Task<TelemetryIngestionResult>>();
        try
        {
            int[] pids = await OpenSessionsAsync(contexts, test.Token);
            writers.Add(test.Start(test.Service(contexts[0]), failedRequest));
            // Detect a failure before the interceptor, rather than waiting indefinitely for its gate.
            if (await Task.WhenAny(injection.Saved.Task, writers[0]) != injection.Saved.Task)
            {
                await writers[0];
                throw new InvalidOperationException("Writer completed without reaching the SQL failure gate.");
            }
            writers.Add(test.Start(test.Service(contexts[1]), successor));
            await using (var observer = await test.OpenConnectionAsync())
                await ObserveWaitersAsync(observer, null, new[] { pids[1] }, writers, test.Token);
            injection.Release.TrySetResult();
            var outcomes = await Task.WhenAll(writers.Select(ObserveOutcomeAsync));
            Assert.Same(injection.Failure, outcomes[0].Error);
            Assert.NotNull(outcomes[1].Result);
            Assert.False(outcomes[1].Result!.Replayed);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => contexts[0].SaveChangesAsync());
            await using var read = test.Context();
            Assert.False(await read.SimulationBatches.AnyAsync(batch => batch.BatchId == failedRequest.BatchId, test.Token));
            Assert.False(await read.TelemetryPings.AnyAsync(ping => ping.BatchId == failedRequest.BatchId, test.Token));
            Assert.Equal(2, await read.TelemetryPings.CountAsync(ping => ping.BatchId == successor.BatchId, test.Token));
            Assert.False(await read.IncidentLogs.AnyAsync(incident => test.DeviceIds.Contains(incident.DeviceId), test.Token));
            await AssertFinalStateAsync(test, read, new[] { successor.BatchId }, "Healthy");
        }
        finally
        {
            injection.Release.TrySetResult();
            await DrainAndDisposeAsync(writers, contexts);
        }
    });

    /// <summary>Opens each participating connection before launch so waiter checks can identify exact PostgreSQL backend PIDs.</summary>
    private static async Task<int[]> OpenSessionsAsync(TelemetryDbContext[] contexts, CancellationToken token)
    {
        var pids = new List<int>();
        foreach (var context in contexts)
        {
            await context.Database.OpenConnectionAsync(token);
            pids.Add(((NpgsqlConnection)context.Database.GetDbConnection()).ProcessID);
        }
        Assert.Equal(contexts.Length, pids.Distinct().Count());
        return pids.ToArray();
    }

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({LockKey})", connection, transaction);
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>Observes only participating sessions and aborts promptly if a writer fails before all expected waiters appear.</summary>
    private static async Task ObserveWaitersAsync(NpgsqlConnection observer, NpgsqlTransaction? transaction, int[] expectedPids,
        IReadOnlyList<Task<TelemetryIngestionResult>> writers, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT count(DISTINCT pid) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted
              AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
              AND pid = ANY(@pids) AND classid::bigint = (@key >> 32) AND objid::bigint = (@key & 4294967295)
            """, observer, transaction);
        command.Parameters.AddWithValue("pids", expectedPids);
        command.Parameters.AddWithValue("key", LockKey);
        while (Convert.ToInt64(await command.ExecuteScalarAsync(token)) < expectedPids.Length)
        {
            var finished = writers.FirstOrDefault(writer => writer.IsCompleted);
            if (finished is not null)
            {
                await finished;
                throw new InvalidOperationException("Writer completed before its expected lock wait was observed.");
            }
            await Task.Delay(10, token);
        }
    }

    /// <summary>Checks expected scenario measurements, incident references, and coherent final state without guessing scheduling order.</summary>
    /// <param name="test">Scenario owning the selected device identities.</param>
    /// <param name="read">Independent read context used after all writers have terminated.</param>
    /// <param name="batches">Only accepted batch identities whose observations may back the final state or incidents.</param>
    /// <param name="scenario">Expected Healthy or HighCpu scenario, obtained from the known requests or winning receipt.</param>
    /// <returns>Completion after scenario values, same-device incident links, and final-state projections are asserted.</returns>
    private static async Task AssertFinalStateAsync(TelemetryIngestionTestScenario test, TelemetryDbContext read, Guid[] batches, string scenario)
    {
        var states = await read.DeviceTelemetryStates.AsNoTracking().Where(state => test.DeviceIds.Contains(state.DeviceId)).ToArrayAsync(test.Token);
        Assert.Equal(2, states.Length);
        var pings = await read.TelemetryPings.AsNoTracking().Where(ping => batches.Contains(ping.BatchId)).ToArrayAsync(test.Token);
        // Assert scenario truth independently of the state-to-ping consistency checks below.
        Assert.All(pings, ping =>
        {
            Assert.Contains(ping.DeviceId, test.DeviceIds);
            Assert.Equal(PingOutcome.Success, ping.Outcome);
            Assert.Equal(40, ping.LatencyMs);
            Assert.Equal(scenario == "HighCpu" ? 95m : 30m, ping.CpuPercent);
        });
        var incidents = await read.IncidentLogs.AsNoTracking()
            .Where(incident => test.DeviceIds.Contains(incident.DeviceId)).ToArrayAsync(test.Token);
        if (scenario == "Healthy") Assert.Empty(incidents);
        else
        {
            Assert.Equal(test.DeviceIds.Length, incidents.Length);
            foreach (Guid deviceId in test.DeviceIds)
            {
                var incident = Assert.Single(incidents, incident => incident.DeviceId == deviceId);
                Assert.Equal(IncidentKind.Cpu, incident.Kind);
                Assert.Null(incident.ResolvedAtUtc);
                Assert.NotNull(incident.OpenedByPingId);
                var opening = Assert.Single(pings, ping => ping.Id == incident.OpenedByPingId && ping.DeviceId == deviceId);
                Assert.Contains(opening.BatchId, batches);
                Assert.Equal(95m, opening.CpuPercent);
                Assert.Equal(opening.ObservedAtUtc, incident.OpenedAtUtc);
            }
        }
        var finalBatches = new HashSet<Guid>();
        foreach (var state in states)
        {
            var ping = Assert.Single(pings, ping => ping.Id == state.LastPingId && ping.DeviceId == state.DeviceId);
            Assert.Equal(ping.ObservedAtUtc, state.LastObservedAtUtc);
            Assert.Equal(ping.ObservedAtUtc, state.LastSuccessfulAtUtc);
            Assert.Equal(ping.Outcome, state.LatestOutcome);
            Assert.Equal(ping.LatencyMs, state.LatestLatencyMs);
            Assert.Equal(ping.CpuPercent, state.LatestCpuPercent);
            finalBatches.Add(ping.BatchId);
        }
        Assert.Single(finalBatches); // One serialized batch must leave a coherent fleet snapshot for these devices.
    }

    /// <summary>Observes every competing result, including intentional conflicts, without abandoning other writers.</summary>
    private static async Task<Outcome> ObserveOutcomeAsync(Task<TelemetryIngestionResult> writer)
    {
        try { return new(await writer, null); }
        catch (Exception error) { return new(null, error); }
    }

    /// <summary>Releases the external barrier before draining; no context or pool may be disposed while its writer is active.</summary>
    private static async Task ReleaseDrainDisposeAsync(NpgsqlConnection observer, NpgsqlTransaction barrier, bool released,
        IReadOnlyList<Task<TelemetryIngestionResult>> writers, TelemetryDbContext[] contexts)
    {
        try
        {
            if (!released)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await barrier.RollbackAsync(timeout.Token); }
                catch { await observer.CloseAsync(); }
            }
        }
        finally { await DrainAndDisposeAsync(writers, contexts); }
    }

    /// <summary>Drains registered work before disposal. The scenario's outer deadline fail-stops instead of racing unresolved writers.</summary>
    private static async Task DrainAndDisposeAsync(IReadOnlyList<Task<TelemetryIngestionResult>> writers, TelemetryDbContext[] contexts)
    {
        try { await Task.WhenAll(writers); }
        catch { /* Bodies assert expected faults; teardown establishes termination before touching owned resources. */ }
        foreach (var context in contexts) await context.DisposeAsync();
    }
}
