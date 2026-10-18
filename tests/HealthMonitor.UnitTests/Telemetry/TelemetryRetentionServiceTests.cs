using HealthMonitor.Application.Telemetry;

namespace HealthMonitor.UnitTests.Telemetry;

/// <summary>Checks Application retention budgets and confirmed progress without claiming any SQL deletion or rollback proof.</summary>
public sealed class TelemetryRetentionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Records policy inputs and supplies explicit batch outcomes without persistence.</summary>
    private sealed class Store : ITelemetryRetentionStore
    {
        /// <summary>Sequential call inputs used to verify fixed cutoffs and cancellation propagation.</summary>
        internal List<(DateTimeOffset Ping, DateTimeOffset History, CancellationToken Token)> Calls { get; } = [];
        /// <summary>Scenario-owned result/failure function; defaults to an empty, committed inspection batch.</summary>
        internal Func<int, CancellationToken, Task<TelemetryRetentionBatchResult>> Outcome { get; set; } =
            (_, _) => Task.FromResult(new TelemetryRetentionBatchResult(0, 0, 0, false));
        /// <inheritdoc />
        public Task<TelemetryRetentionBatchResult> PruneBatchAsync(DateTimeOffset pingObservedBeforeUtc,
            DateTimeOffset historyBeforeUtc, CancellationToken cancellationToken = default)
        {
            Calls.Add((pingObservedBeforeUtc, historyBeforeUtc, cancellationToken));
            return Outcome(Calls.Count, cancellationToken);
        }
    }

    /// <summary>Changes wall time inside a pass to prove cutoffs are captured only once.</summary>
    private sealed class Clock : TimeProvider
    {
        /// <summary>Current test UTC instant.</summary>
        internal DateTimeOffset Utc { get; set; } = Now;
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => Utc;
    }

    [Fact]
    public void Constructor_NullStore_Throws() => Assert.Throws<ArgumentNullException>(() => new TelemetryRetentionService(null!, new Clock()));

    [Fact]
    public void Constructor_NullClock_Throws() => Assert.Throws<ArgumentNullException>(() => new TelemetryRetentionService(new Store(), null!));

    [Fact]
    public async Task EmptyBatch_StopsAfterOneCommittedInspection()
    {
        var store = new Store();
        var service = new TelemetryRetentionService(store, new Clock());
        Assert.Null(service.LastConfirmedProgress);
        var result = await service.RunPassAsync();
        Assert.Equal(new TelemetryRetentionPassResult(1, 0, 0, 0, false), result);
        Assert.Same(result, service.LastConfirmedProgress);
        Assert.Single(store.Calls);
        Assert.Equal(Now.AddHours(-24), store.Calls[0].Ping);
        Assert.Equal(Now.AddDays(-7), store.Calls[0].History);
        Assert.Equal(TimeSpan.Zero, store.Calls[0].Ping.Offset);
        Assert.Equal(TimeSpan.Zero, store.Calls[0].History.Offset);
    }

    [Fact]
    public async Task MultipleBatches_AccumulateCountsAndKeepPassCutoffsFixed()
    {
        var clock = new Clock();
        var store = new Store
        {
            Outcome = (call, _) =>
            {
                clock.Utc += TimeSpan.FromHours(1);
                return Task.FromResult(new TelemetryRetentionBatchResult(call, call * 2, call * 3, call < 3));
            }
        };
        var result = await new TelemetryRetentionService(store, clock).RunPassAsync();
        Assert.Equal(new TelemetryRetentionPassResult(3, 6, 12, 18, false), result);
        Assert.All(store.Calls, call =>
        {
            Assert.Equal(Now.AddHours(-24), call.Ping);
            Assert.Equal(Now.AddDays(-7), call.History);
        });
    }

    [Theory]
    [InlineData(1000, 0, 0)]
    [InlineData(0, 1000, 0)]
    [InlineData(0, 0, 1000)]
    [InlineData(334, 333, 333)]
    public async Task ExactCombinedBudget_IsAcceptedAndStopsAtTenBatches(int pings, int incidents, int receipts)
    {
        var store = new Store { Outcome = (_, _) => Task.FromResult(new TelemetryRetentionBatchResult(pings, incidents, receipts, true)) };
        var result = await new TelemetryRetentionService(store, new Clock()).RunPassAsync();
        Assert.Equal(10, store.Calls.Count);
        Assert.Equal(10, result.CompletedBatches);
        Assert.Equal(pings * 10, result.DeletedPings);
        Assert.Equal(incidents * 10, result.DeletedIncidents);
        Assert.Equal(receipts * 10, result.DeletedReceipts);
        Assert.Equal(10000, result.DeletedPings + result.DeletedIncidents + result.DeletedReceipts);
        Assert.True(result.HasBacklog);
    }

    [Fact]
    public async Task TenthBatchClearsBacklog_DoesNotRequestEleventh()
    {
        var store = new Store { Outcome = (call, _) => Task.FromResult(new TelemetryRetentionBatchResult(1, 0, 0, call < 10)) };
        var result = await new TelemetryRetentionService(store, new Clock()).RunPassAsync();
        Assert.Equal(10, store.Calls.Count);
        Assert.False(result.HasBacklog);
    }

    [Fact]
    public async Task ZeroDeletionWithBacklog_StopsWithoutSpinningOrHidingBacklog()
    {
        var store = new Store { Outcome = (_, _) => Task.FromResult(new TelemetryRetentionBatchResult(0, 0, 0, true)) };
        var result = await new TelemetryRetentionService(store, new Clock()).RunPassAsync();
        Assert.Single(store.Calls);
        Assert.Equal(0, result.DeletedPings + result.DeletedIncidents + result.DeletedReceipts);
        Assert.True(result.HasBacklog);
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, -1)]
    [InlineData(1001, 0, 0)]
    [InlineData(0, 1001, 0)]
    [InlineData(0, 0, 1001)]
    [InlineData(500, 500, 1)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue)]
    public async Task InvalidBatchCounts_AreRejectedWithoutAdoptingUnverifiedProgress(int pings, int incidents, int receipts)
    {
        var store = new Store { Outcome = (_, _) => Task.FromResult(new TelemetryRetentionBatchResult(pings, incidents, receipts, true)) };
        var service = new TelemetryRetentionService(store, new Clock());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunPassAsync());
        Assert.Null(service.LastConfirmedProgress);
        Assert.Single(store.Calls);
    }

    [Fact]
    public async Task NullBatchResult_IsRejected()
    {
        var store = new Store { Outcome = (_, _) => Task.FromResult<TelemetryRetentionBatchResult>(null!) };
        var service = new TelemetryRetentionService(store, new Clock());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunPassAsync());
        Assert.Null(service.LastConfirmedProgress);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task StoreFailure_PropagatesSameExceptionAndOnlyConfirmedProgress(int completed)
    {
        var error = new InvalidOperationException("failure may follow an uncertain commit");
        var store = new Store { Outcome = (call, _) => call > completed
            ? throw error : Task.FromResult(new TelemetryRetentionBatchResult(7, 2, 1, true)) };
        var service = new TelemetryRetentionService(store, new Clock());
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunPassAsync()));
        Assert.Equal(completed + 1, store.Calls.Count);
        if (completed == 0) Assert.Null(service.LastConfirmedProgress);
        else Assert.Equal(new TelemetryRetentionPassResult(2, 14, 4, 2, true), service.LastConfirmedProgress);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunPassAsync());
        Assert.Equal(completed + 1, store.Calls.Count); // Failed context ownership is never reused.
    }

    [Fact]
    public async Task InvalidLaterResult_PreservesEarlierConfirmedProgress()
    {
        var store = new Store { Outcome = (call, _) => Task.FromResult(call == 1
            ? new TelemetryRetentionBatchResult(4, 3, 2, true) : new TelemetryRetentionBatchResult(1001, 0, 0, true)) };
        var service = new TelemetryRetentionService(store, new Clock());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunPassAsync());
        Assert.Equal(new TelemetryRetentionPassResult(1, 4, 3, 2, true), service.LastConfirmedProgress);
    }

    [Fact]
    public async Task PreCancelled_DoesNotAdmitOrAccessStore()
    {
        var store = new Store();
        var service = new TelemetryRetentionService(store, new Clock());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => service.RunPassAsync(cancelled.Token));
        Assert.Equal(cancelled.Token, error.CancellationToken);
        Assert.Empty(store.Calls);
        Assert.Null(service.LastConfirmedProgress);
        Assert.Equal(1, (await service.RunPassAsync()).CompletedBatches);
    }

    [Fact]
    public async Task CancellationBetweenBatches_ReportsConfirmedBatchAndDoesNotStartAnother()
    {
        using var cancelled = new CancellationTokenSource();
        var store = new Store { Outcome = (_, _) =>
        {
            cancelled.Cancel();
            return Task.FromResult(new TelemetryRetentionBatchResult(4, 3, 2, true));
        } };
        var service = new TelemetryRetentionService(store, new Clock());
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => service.RunPassAsync(cancelled.Token));
        Assert.Equal(cancelled.Token, error.CancellationToken);
        Assert.Single(store.Calls);
        Assert.Equal(cancelled.Token, store.Calls[0].Token);
        Assert.Equal(new TelemetryRetentionPassResult(1, 4, 3, 2, true), service.LastConfirmedProgress);
    }

    [Fact]
    public async Task StoreCancellation_PropagatesOriginalFailureAndDoesNotInventProgress()
    {
        using var cancellation = new CancellationTokenSource();
        var error = new OperationCanceledException(cancellation.Token);
        var store = new Store { Outcome = (_, _) => throw error };
        var service = new TelemetryRetentionService(store, new Clock());
        Assert.Same(error, await Assert.ThrowsAsync<OperationCanceledException>(() => service.RunPassAsync(cancellation.Token)));
        Assert.Null(service.LastConfirmedProgress);
        Assert.Single(store.Calls);
    }

    [Fact]
    public async Task CancellationAfterFinalConfirmedResult_DoesNotRevokeItsCounts()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new Store { Outcome = (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(new TelemetryRetentionBatchResult(4, 3, 2, false));
        } };
        var service = new TelemetryRetentionService(store, new Clock());
        Assert.Equal(new TelemetryRetentionPassResult(1, 4, 3, 2, false), await service.RunPassAsync(cancellation.Token));
    }

    [Fact]
    public async Task ConcurrentOrRepeatedPass_IsRejectedBeforeAnotherStoreCall()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Store { Outcome = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return new(0, 0, 0, false);
        } };
        var service = new TelemetryRetentionService(store, new Clock());
        var running = service.RunPassAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunPassAsync());
            Assert.Single(store.Calls);
        }
        finally { release.TrySetResult(); }
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunPassAsync());
        Assert.Single(store.Calls);
    }
}
