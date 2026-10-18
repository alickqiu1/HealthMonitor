using System.Data.Common;
using HealthMonitor.Application.IntegrationContracts;
using HealthMonitor.Application.Telemetry;
using HealthMonitor.Domain.Telemetry;
using HealthMonitor.Infrastructure.Data.TelemetryContext;
using HealthMonitor.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HealthMonitor.IntegrationTests.Telemetry;

/// <summary>Verifies real PostgreSQL history snapshots using only fixture-owned devices, reserved receipts, and bounded gates.</summary>
/// <remarks>No Device writes, migrations, unrelated cleanup, or hosted connections. Registered tasks terminate before contexts and owned data are disposed.</remarks>
[Collection(TelemetryIngestionDatabaseCollection.Name)]
public sealed class DeviceHistoryQueriesTests
{
    /// <summary>Seeds small observation sets after reserving every receipt; the scenario owns all inserted identities.</summary>
    /// <param name="test">Verified isolated scenario.</param>
    /// <param name="device">Index of one of the two proven-absent owned devices.</param>
    /// <param name="times">Explicit UTC observation times; alternating success/timeout proves nullable projection.</param>
    /// <returns>Written facts for independently constructed expected projections.</returns>
    private static Task<TelemetryPing[]> SeedPingsAsync(TelemetryIngestionTestScenario test, int device, params DateTimeOffset[] times)
    {
        return test.TrackWriter(WriteAsync());
        async Task<TelemetryPing[]> WriteAsync()
        {
            var batches = times.Select(_ => Guid.NewGuid()).ToArray();
            await test.ReserveBatchIdsAsync(batches);
            var pings = times.Select((time, index) => index % 2 == 0
                ? TelemetryPing.CreateSuccess(Guid.NewGuid(), test.DeviceIds[device], batches[index], time, 40 + index, 30m)
                : TelemetryPing.CreateTimeout(Guid.NewGuid(), test.DeviceIds[device], batches[index], time)).ToArray();
            await using var context = test.Context();
            foreach (var ping in pings)
            {
                context.SimulationBatches.Add(SimulationBatch.CreateCommitted(ping.BatchId, new string('a', 64),
                    ping.Outcome == PingOutcome.Success ? "Healthy" : "Timeout", ping.ObservedAtUtc, ping.ObservedAtUtc, 1));
                context.TelemetryPings.Add(ping);
            }
            await context.SaveChangesAsync(test.Token);
            return pings;
        }
    }

    /// <summary>Seeds incident histories while respecting the unique open-kind constraint and nullable retained ping references.</summary>
    /// <param name="test">Owner of device, receipt, and incident identities.</param>
    /// <param name="specs">Explicit opening times, lifecycle membership, kinds, and owned device indices.</param>
    /// <returns>Written incident facts.</returns>
    private static Task<IncidentLog[]> SeedIncidentsAsync(TelemetryIngestionTestScenario test,
        params (DateTimeOffset Opened, bool Resolved, IncidentKind Kind, int Device)[] specs)
    {
        return test.TrackWriter(WriteAsync());
        async Task<IncidentLog[]> WriteAsync()
        {
            var pings = new[]
            {
                (await SeedPingsAsync(test, 0, specs.Min(s => s.Opened).AddMinutes(-1)))[0],
                (await SeedPingsAsync(test, 1, specs.Min(s => s.Opened).AddMinutes(-1)))[0]
            };
            var incidents = specs.Select(spec =>
            {
                var incident = IncidentLog.Open(Guid.NewGuid(), test.DeviceIds[spec.Device], spec.Kind,
                    spec.Opened, pings[spec.Device].Id, $"History {spec.Kind}");
                if (spec.Resolved) incident.Resolve(spec.Opened.AddHours(1), pings[spec.Device].Id);
                return incident;
            }).ToArray();
            await using var context = test.Context();
            context.IncidentLogs.AddRange(incidents);
            await context.SaveChangesAsync(test.Token);
            return incidents;
        }
    }

    /// <summary>Observes actual transaction mode and gates the page only after the count reader has completed.</summary>
    /// <remarks>SHOW commands use the same connection/transaction before the EF reader starts; no second active reader or write is introduced.</remarks>
    private sealed class PageProbe : DbCommandInterceptor
    {
        internal Func<CancellationToken, Task>? BeforePage;
        internal long? ExpectedOffset;
        internal bool SawLongOffset, SawReadOnly;
        internal int Readers;

        /// <inheritdoc />
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Readers++;
            if (Readers == 1)
            {
                await using var check = command.Connection!.CreateCommand();
                check.Transaction = command.Transaction;
                check.CommandText = "SHOW transaction_read_only";
                Assert.Equal("on", await check.ExecuteScalarAsync(cancellationToken));
                check.CommandText = "SHOW transaction_isolation";
                Assert.Equal("repeatable read", await check.ExecuteScalarAsync(cancellationToken));
                SawReadOnly = true;
            }
            if (Readers == 2)
            {
                if (ExpectedOffset.HasValue)
                    SawLongOffset = command.Parameters.Cast<DbParameter>().Any(p => p.Value is long value && value == ExpectedOffset.Value);
                if (BeforePage is not null) await BeforePage(cancellationToken);
            }
            return result;
        }
    }

    /// <summary>Independent expected observation projection, including success/timeout nullability.</summary>
    private static TelemetryHistoryItem Ping(TelemetryPing p) => new(p.Id, p.DeviceId, p.ObservedAtUtc, p.Outcome, p.LatencyMs, p.CpuPercent);
    /// <summary>Independent expected incident projection, without persistence entities escaping the adapter.</summary>
    private static IncidentHistoryItem Incident(IncidentLog i) => new(i.Id, i.DeviceId, i.Kind, i.OpenedAtUtc, i.ResolvedAtUtc, i.Summary);
    /// <summary>Creates a broad valid range around the scenario time for tests not targeting range predicates.</summary>
    private static TelemetryHistoryRange Range(TelemetryIngestionTestScenario test) => new(test.UtcNow.AddDays(-1), test.UtcNow.AddDays(1));

    [Fact]
    public Task TelemetryRange_ProjectsHalfOpenBoundsAndSelectedDeviceUntracked() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        var from = test.UtcNow.AddHours(-1); var to = test.UtcNow;
        var pings = await SeedPingsAsync(test, 0, from.AddTicks(-10), from, from.AddMinutes(1), to.AddTicks(-10), to, to.AddTicks(10));
        await SeedPingsAsync(test, 1, from);
        var probe = new PageProbe();
        await using var context = test.Context(probe);
        var page = await test.TrackWriter(new DeviceHistoryQueries(context).GetTelemetryAsync(test.DeviceIds[0], new(), new(from, to), test.Token));
        var expected = pings.Where(p => p.ObservedAtUtc >= from && p.ObservedAtUtc < to)
            .OrderByDescending(p => p.ObservedAtUtc).ThenByDescending(p => p.Id).Select(Ping);
        Assert.Equal(expected, page.Items);
        Assert.Equal(3L, page.TotalCount);
        Assert.True(probe.SawReadOnly);
        Assert.Equal(2, probe.Readers);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Throws<NotSupportedException>(() => ((IList<TelemetryHistoryItem>)page.Items).Clear());
        // Residual .NET ticks must not silently widen/narrow a half-open PostgreSQL microsecond window.
        await using var preciseContext = test.Context();
        var precise = new DeviceHistoryQueries(preciseContext);
        foreach (var window in new[] { new TelemetryHistoryRange(from.AddTicks(1), to.AddTicks(1)),
            new TelemetryHistoryRange(from.AddTicks(1), from.AddTicks(2)), new TelemetryHistoryRange(from, from.AddTicks(10)) })
        {
            var precisePage = await test.TrackWriter(precise.GetTelemetryAsync(test.DeviceIds[0], new(), window, test.Token));
            var matches = pings.Where(p => p.ObservedAtUtc >= window.FromUtc && p.ObservedAtUtc < window.ToUtc)
                .OrderByDescending(p => p.ObservedAtUtc).ThenByDescending(p => p.Id).Select(Ping).ToArray();
            Assert.Equal(matches, precisePage.Items);
            Assert.Equal((long)matches.Length, precisePage.TotalCount);
        }
        var latestFinite = DateTimeOffset.MaxValue.AddTicks(-9);
        var high = await SeedPingsAsync(test, 0, latestFinite.AddTicks(-10), latestFinite);
        var upperPage = await test.TrackWriter(precise.GetTelemetryAsync(test.DeviceIds[0], new(),
            new(latestFinite, DateTimeOffset.MaxValue), test.Token));
        Assert.Equal(new[] { Ping(high[1]) }, upperPage.Items);
        Assert.Equal(1L, upperPage.TotalCount);
    });

    [Fact]
    public Task TelemetryPages_OrderTimestampUuidAndPreserveTrueCount() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        var pings = await SeedPingsAsync(test, 0, test.UtcNow, test.UtcNow, test.UtcNow, test.UtcNow.AddSeconds(-1), test.UtcNow.AddSeconds(1));
        var expected = pings.OrderByDescending(p => p.ObservedAtUtc).ThenByDescending(p => p.Id).Select(Ping).ToArray();
        await using var context = test.Context();
        var query = new DeviceHistoryQueries(context);
        var found = new List<TelemetryHistoryItem>();
        for (int number = 1; number <= 4; number++)
        {
            var page = await test.TrackWriter(query.GetTelemetryAsync(test.DeviceIds[0], new(number, 2), Range(test), test.Token));
            Assert.Equal(5L, page.TotalCount);
            Assert.Equal(number, page.Paging.Page);
            Assert.Equal(expected.Skip((number - 1) * 2).Take(2), page.Items);
            found.AddRange(page.Items);
        }
        Assert.Equal(expected, found);
        Assert.Empty(context.ChangeTracker.Entries());
    });

    [Fact]
    public Task HugeOffset_UsesInt64ParameterAndReturnsTrueCount() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        await SeedPingsAsync(test, 0, test.UtcNow);
        await SeedIncidentsAsync(test, (test.UtcNow, false, IncidentKind.Cpu, 0));
        var paging = new HistoryPaging(int.MaxValue, 100);
        var pingProbe = new PageProbe { ExpectedOffset = paging.Offset };
        await using var pingContext = test.Context(pingProbe);
        var pings = await test.TrackWriter(new DeviceHistoryQueries(pingContext).GetTelemetryAsync(test.DeviceIds[0], paging,
            new(test.UtcNow.AddDays(-1).AddTicks(1), test.UtcNow.AddDays(1).AddTicks(1)), test.Token));
        Assert.Empty(pings.Items); Assert.Equal(2L, pings.TotalCount); Assert.True(pingProbe.SawLongOffset);
        foreach (var state in Enum.GetValues<IncidentHistoryState>())
        {
            var probe = new PageProbe { ExpectedOffset = paging.Offset };
            await using var context = test.Context(probe);
            var page = await test.TrackWriter(new DeviceHistoryQueries(context).GetIncidentsAsync(test.DeviceIds[0], paging, state, test.Token));
            Assert.Empty(page.Items);
            Assert.Equal(state == IncidentHistoryState.Resolved ? 0L : 1L, page.TotalCount);
            Assert.True(probe.SawLongOffset);
            Assert.Empty(context.ChangeTracker.Entries());
        }
    });

    [Fact]
    public Task TelemetryCountAndPage_PreserveSnapshotDuringConcurrentInsert() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        await SeedPingsAsync(test, 0, test.UtcNow);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new PageProbe { BeforePage = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); } };
        var context = test.Context(probe);
        Task<HistoryPage<TelemetryHistoryItem>>? read = null;
        try
        {
            read = test.TrackWriter(new DeviceHistoryQueries(context).GetTelemetryAsync(test.DeviceIds[0], new(), Range(test), test.Token));
            await Task.WhenAny(entered.Task, read).WaitAsync(test.Token);
            if (!entered.Task.IsCompleted) await read;
            await entered.Task.WaitAsync(test.Token);
            await SeedPingsAsync(test, 0, test.UtcNow.AddSeconds(1));
            release.TrySetResult();
            var page = await read;
            Assert.Equal(1L, page.TotalCount); Assert.Single(page.Items); Assert.True(probe.SawReadOnly);
            await using var fresh = test.Context();
            Assert.Equal(2L, (await test.TrackWriter(new DeviceHistoryQueries(fresh).GetTelemetryAsync(test.DeviceIds[0], new(), Range(test), test.Token))).TotalCount);
        }
        finally
        {
            release.TrySetResult();
            if (read is not null) await TelemetryIngestionTestScenario.DrainWritersAsync(read);
            await context.DisposeAsync();
        }
    });

    [Fact]
    public Task IncidentPages_FilterStateAndOrderTimestampUuid() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        var old = test.UtcNow.AddDays(-10);
        var incidents = await SeedIncidentsAsync(test,
            (old, false, IncidentKind.Cpu, 0), (old, false, IncidentKind.Latency, 0),
            (old, true, IncidentKind.Cpu, 0), (old.AddSeconds(-1), true, IncidentKind.Cpu, 0),
            (old.AddSeconds(1), true, IncidentKind.Cpu, 0), (old, false, IncidentKind.Cpu, 1));
        var expected = incidents.Where(i => i.DeviceId == test.DeviceIds[0])
            .OrderByDescending(i => i.OpenedAtUtc).ThenByDescending(i => i.Id).Select(Incident).ToArray();
        await using var context = test.Context();
        var query = new DeviceHistoryQueries(context);
        foreach (var state in Enum.GetValues<IncidentHistoryState>())
        {
            var filtered = expected.Where(i => state == IncidentHistoryState.All ||
                (state == IncidentHistoryState.Open && i.ResolvedAtUtc is null) ||
                (state == IncidentHistoryState.Resolved && i.ResolvedAtUtc is not null)).ToArray();
            for (int number = 1; number <= 4; number++)
            {
                var page = await test.TrackWriter(query.GetIncidentsAsync(test.DeviceIds[0], new(number, 2), state, test.Token));
                Assert.Equal((long)filtered.Length, page.TotalCount);
                Assert.Equal(filtered.Skip((number - 1) * 2).Take(2), page.Items);
                Assert.Throws<NotSupportedException>(() => ((IList<IncidentHistoryItem>)page.Items).Clear());
            }
        }
        Assert.Empty(context.ChangeTracker.Entries());
    });

    [Fact]
    public Task IncidentCountAndPage_PreserveOpenMembershipDuringConcurrentResolution() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        var incidents = await SeedIncidentsAsync(test, (test.UtcNow, false, IncidentKind.Cpu, 0), (test.UtcNow, false, IncidentKind.Latency, 0));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new PageProbe { BeforePage = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); } };
        var context = test.Context(probe);
        Task<HistoryPage<IncidentHistoryItem>>? read = null;
        try
        {
            read = test.TrackWriter(new DeviceHistoryQueries(context).GetIncidentsAsync(test.DeviceIds[0], new(), IncidentHistoryState.Open, test.Token));
            await Task.WhenAny(entered.Task, read).WaitAsync(test.Token);
            if (!entered.Task.IsCompleted) await read;
            await entered.Task.WaitAsync(test.Token);
            await test.TrackWriter(ResolveAsync());
            release.TrySetResult();
            var page = await read;
            Assert.Equal(2L, page.TotalCount); Assert.Equal(2, page.Items.Count);
            Assert.All(page.Items, item => Assert.Null(item.ResolvedAtUtc));
            await using var fresh = test.Context();
            Assert.Equal(1L, (await test.TrackWriter(new DeviceHistoryQueries(fresh).GetIncidentsAsync(test.DeviceIds[0], new(), IncidentHistoryState.Open, test.Token))).TotalCount);
        }
        finally
        {
            release.TrySetResult();
            if (read is not null) await TelemetryIngestionTestScenario.DrainWritersAsync(read);
            await context.DisposeAsync();
        }
        async Task<bool> ResolveAsync()
        {
            await using var writer = test.Context();
            var incident = await writer.IncidentLogs.SingleAsync(i => i.Id == incidents[0].Id, test.Token);
            incident.Resolve(test.UtcNow.AddSeconds(1), incident.OpenedByPingId!.Value);
            await writer.SaveChangesAsync(test.Token);
            return true;
        }
    });

    [Fact]
    public Task CancellationAfterCount_PropagatesAndDisposesTransaction() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(test.Token);
        var probe = new PageProbe { BeforePage = token => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; } };
        await using var context = test.Context(probe);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.TrackWriter(
            new DeviceHistoryQueries(context).GetTelemetryAsync(test.DeviceIds[0], new(), Range(test), cancellation.Token)));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(2, probe.Readers);
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Empty(context.ChangeTracker.Entries());
    });

    [Fact]
    public Task OriginalReadFailure_SurvivesTransactionCleanupFailure() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        var failure = new InvalidOperationException("Owned history page failure");
        var probe = new PageProbe { BeforePage = _ => throw failure };
        await using var context = test.CleanupFailureContext(probe);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => test.TrackWriter(
            new DeviceHistoryQueries(context).GetIncidentsAsync(test.DeviceIds[0], new(), cancellationToken: test.Token))));
        Assert.Equal(2, probe.Readers);
        Assert.Null(context.Database.CurrentTransaction);
    });

    [Fact]
    public Task ExistingTransaction_IsRejectedWithoutChangingCallerWork() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        var ping = (await SeedPingsAsync(test, 0, test.UtcNow))[0];
        await using var context = test.Context();
        await using var caller = await context.Database.BeginTransactionAsync(test.Token);
        var tracked = await context.TelemetryPings.SingleAsync(p => p.Id == ping.Id, test.Token);
        context.Entry(tracked).State = EntityState.Modified; // Sentinel caller work; it must never be saved or discarded by a read.
        var query = new DeviceHistoryQueries(context);
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.TrackWriter(query.GetTelemetryAsync(test.DeviceIds[0], new(), Range(test), test.Token)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.TrackWriter(query.GetIncidentsAsync(test.DeviceIds[0], new(), cancellationToken: test.Token)));
        Assert.Same(caller, context.Database.CurrentTransaction);
        Assert.Same(tracked, Assert.Single(context.ChangeTracker.Entries()).Entity);
        Assert.Equal(EntityState.Modified, context.Entry(tracked).State);
        await caller.RollbackAsync(test.Token);
    });
}
