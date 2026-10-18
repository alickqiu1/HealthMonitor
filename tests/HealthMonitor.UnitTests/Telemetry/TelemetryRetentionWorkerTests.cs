using HealthMonitor.Api.Composition;
using HealthMonitor.Application.Telemetry;
using HealthMonitor.Domain.Telemetry;
using HealthMonitor.Infrastructure.Data.TelemetryContext;
using HealthMonitor.UnitTests.Producer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HealthMonitor.UnitTests.Telemetry;

/// <summary>Verifies retention scheduling and recovery using the real Application pass coordinator and an explicit clock.</summary>
public sealed class TelemetryRetentionWorkerTests
{
    [Fact]
    public async Task Registration_IndependentOfDisabledProducer_PreservesClockAndScopedContext()
    {
        var services = new ServiceCollection();
        var clock = new ProducerTestClock();
        services.AddSingleton<TimeProvider>(clock);
        services.AddLogging();
        services.AddDbContext<TelemetryDbContext>(options => options.UseNpgsql("Host=example.invalid;Database=fictional;Username=unused"));
        services.AddDemoProducer(new ConfigurationBuilder().Build());
        Assert.Same(services, services.AddTelemetryRetention());
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, item => item.ServiceType == typeof(ITelemetryRetentionStore)).Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, item => item.ServiceType == typeof(TelemetryRetentionService)).Lifetime);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        Assert.Same(clock, provider.GetRequiredService<TimeProvider>());
        Assert.Single(provider.GetServices<IHostedService>());
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        Assert.NotSame(first.ServiceProvider.GetRequiredService<ITelemetryRetentionStore>(), second.ServiceProvider.GetRequiredService<ITelemetryRetentionStore>());
        var context = first.ServiceProvider.GetRequiredService<TelemetryDbContext>();
        var now = clock.GetUtcNow();
        var receipt = SimulationBatch.CreateCommitted(Guid.NewGuid(), new string('a', 64), "Healthy", now, now, 1);
        context.SimulationBatches.Add(receipt);
        // The resolved coordinator must hit the guard on this exact scoped context, without opening a connection.
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.ServiceProvider.GetRequiredService<TelemetryRetentionService>().RunPassAsync());
        Assert.Same(receipt, Assert.Single(context.ChangeTracker.Entries()).Entity);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task StartupThenExactHourlyBoundary_UsesFreshDisposedScopes()
    {
        await using var rig = new RetentionWorkerTestRig();
        await rig.Worker.StartAsync(CancellationToken.None);
        await rig.ExpectDelayAsync(3600);
        Assert.Equal(1, rig.Calls);
        Assert.True(rig.Scopes.Single().Disposed);
        rig.Clock.Advance(TimeSpan.FromSeconds(3599));
        Assert.Equal(1, rig.Calls);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        await rig.ExpectDelayAsync(3600);
        Assert.Equal(2, rig.Calls);
        Assert.Equal(2, rig.Scopes.Select(scope => scope.Id).Distinct().Count());
        Assert.All(rig.Scopes, scope => Assert.True(scope.Disposed));
    }

    [Fact]
    public async Task SlowPass_SkipsMissedHoursWithoutOverlapOrCatchup()
    {
        await using var rig = new RetentionWorkerTestRig();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Prune = async (call, token) =>
        {
            if (call == 1) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            return new(0, 0, 0, false);
        };
        await rig.Worker.StartAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            rig.Clock.Advance(TimeSpan.FromSeconds(7201));
            Assert.Equal(1, rig.Calls);
            release.TrySetResult();
            await rig.ExpectDelayAsync(3599);
            rig.Clock.Advance(TimeSpan.FromSeconds(3599));
            await rig.ExpectDelayAsync(3600);
            Assert.Equal(2, rig.Calls);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PassOrCleanupFailure_BackoffDoublesAndCapsWithoutLeakingProviderDetails(bool cleanup)
    {
        await using var rig = new RetentionWorkerTestRig();
        if (cleanup) rig.Cleanup = _ => throw new InvalidOperationException("SENSITIVE cleanup");
        else rig.Prune = (_, _) => throw new InvalidOperationException("SENSITIVE provider");
        await rig.Worker.StartAsync(CancellationToken.None);
        int[] delays = [60, 120, 240, 480, 960, 1920, 3600, 3600];
        for (int index = 0; index < delays.Length; index++)
        {
            await rig.ExpectDelayAsync(delays[index]);
            Assert.Equal(index + 1, rig.Calls);
            Assert.All(rig.Scopes, scope => Assert.True(scope.Disposed));
            if (index + 1 < delays.Length) rig.Clock.Advance(TimeSpan.FromSeconds(delays[index]));
        }
        Assert.All(rig.Logs, log => { Assert.Null(log.Error); Assert.DoesNotContain("SENSITIVE", log.Message); });
        Assert.Contains(rig.Logs, log => log.Message.Contains(cleanup ? "scope cleanup failed" : "pass failed"));
    }

    [Fact]
    public async Task SuccessAfterFailure_ReturnsToAnchoredHourAndResetsBackoff()
    {
        await using var rig = new RetentionWorkerTestRig();
        rig.Prune = (call, _) => call == 3 ? Task.FromResult(new TelemetryRetentionBatchResult(0, 0, 0, false))
            : throw new InvalidOperationException("Unavailable");
        await rig.Worker.StartAsync(CancellationToken.None);
        await rig.ExpectDelayAsync(60);
        rig.Clock.Advance(TimeSpan.FromSeconds(60));
        await rig.ExpectDelayAsync(120);
        rig.Clock.Advance(TimeSpan.FromSeconds(120));
        await rig.ExpectDelayAsync(3420);
        rig.Clock.Advance(TimeSpan.FromSeconds(3420));
        await rig.ExpectDelayAsync(60);
        Assert.Equal(4, rig.Calls);
    }

    [Fact]
    public async Task CommittedPassThenCleanupFailure_KeepsCountsAndRetriesFreshScope()
    {
        await using var rig = new RetentionWorkerTestRig();
        rig.Prune = (_, _) => Task.FromResult(new TelemetryRetentionBatchResult(3, 2, 1, false));
        rig.Cleanup = id => id == 1 ? throw new InvalidOperationException("SENSITIVE") : Task.CompletedTask;
        await rig.Worker.StartAsync(CancellationToken.None);
        await rig.ExpectDelayAsync(60);
        Assert.Contains(rig.Logs, log => log.Message.Contains("scope cleanup failed") && log.Batches == 1 && log.Rows == 6);
        Assert.DoesNotContain(rig.Logs, log => log.Message.Contains("pass failed"));
        rig.Clock.Advance(TimeSpan.FromSeconds(60));
        await rig.ExpectDelayAsync(3540);
        Assert.Equal(2, rig.Scopes.Count);
        Assert.All(rig.Scopes, scope => Assert.True(scope.Disposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedLaterBatch_ReportsOnlyConfirmedProgressEvenWhenCleanupAlsoFails(bool cleanup)
    {
        await using var rig = new RetentionWorkerTestRig();
        rig.Prune = (call, _) => call == 1 ? Task.FromResult(new TelemetryRetentionBatchResult(3, 2, 1, true))
            : throw new InvalidOperationException("SENSITIVE later batch");
        if (cleanup) rig.Cleanup = _ => throw new InvalidOperationException("SENSITIVE disposal");
        await rig.Worker.StartAsync(CancellationToken.None);
        await rig.ExpectDelayAsync(60);
        Assert.Contains(rig.Logs, log => log.Message.Contains("pass failed") && log.Batches == 1 && log.Rows == 6 && log.Message.Contains("unconfirmed commit may exist"));
        if (cleanup) Assert.Contains(rig.Logs, log => log.Message.Contains("scope cleanup failed") && log.Batches == 1 && log.Rows == 6);
        Assert.All(rig.Logs, log => { Assert.Null(log.Error); Assert.DoesNotContain("SENSITIVE", log.Message); });
    }

    [Fact]
    public async Task ZeroProgressBacklog_ReportsBacklogAndWaitsForNextHour()
    {
        await using var rig = new RetentionWorkerTestRig();
        rig.Prune = (_, _) => Task.FromResult(new TelemetryRetentionBatchResult(0, 0, 0, true));
        await rig.Worker.StartAsync(CancellationToken.None);
        await rig.ExpectDelayAsync(3600);
        Assert.Equal(1, rig.Calls);
        Assert.Contains(rig.Logs, log => log.Message.Contains("backlog remains"));
        Assert.Contains(rig.Logs, log => log.Batches == 1 && log.Rows == 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownDuringHourlyOrBackoffDelay_PreventsAnotherAttempt(bool failing)
    {
        await using var rig = new RetentionWorkerTestRig();
        if (failing) rig.Prune = (_, _) => throw new InvalidOperationException("Unavailable");
        await rig.Worker.StartAsync(CancellationToken.None);
        await rig.ExpectDelayAsync(failing ? 60 : 3600);
        await rig.Worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        rig.Clock.Advance(TimeSpan.FromHours(5));
        Assert.Equal(1, rig.Calls);
        Assert.All(rig.Scopes, scope => Assert.True(scope.Disposed));
    }

    [Fact]
    public async Task ShutdownDuringStoreWork_ObservesCancellationThenDisposesScopeWithoutRollbackClaim()
    {
        await using var rig = new RetentionWorkerTestRig();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Prune = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(0, 0, 0, false);
        };
        await rig.Worker.StartAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await rig.Worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(rig.Worker.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Equal(1, rig.Calls);
        Assert.All(rig.Scopes, scope => Assert.True(scope.Disposed));
        Assert.All(rig.Logs, log => Assert.DoesNotContain("rollback", log.Message, StringComparison.OrdinalIgnoreCase));
    }
}
