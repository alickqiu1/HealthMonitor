using HealthMonitor.Api.Composition;
using HealthMonitor.Api.Provisioning;
using HealthMonitor.Application.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HealthMonitor.UnitTests.Producer;

/// <summary>Checks real producer scheduling, immutable recovery, and scope ownership without database or HTTP execution.</summary>
public sealed class DemoTelemetryProducerTests
{
    private static readonly Guid FirstId = FixedDeviceCatalog.Devices[0].Id;

    /// <summary>Constructs nonsecret producer-only configuration; no host configuration or user secrets are loaded.</summary>
    private static IConfiguration Config(string? enabled = null, string? interval = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["DemoProducer:Enabled"] = enabled, ["DemoProducer:IntervalSeconds"] = interval }).Build();

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void Disabled_DoesNotRegisterWorkerOrResolveDependencies(string? enabled)
    {
        var services = new ServiceCollection();
        // An unrelated cadence setting while disabled must not activate a worker or require scoped services.
        var configuration = enabled is null ? new ConfigurationBuilder().Build() : Config(enabled, "1");
        Assert.Same(services, services.AddDemoProducer(configuration));
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(IHostedService));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.Empty(provider.GetServices<IHostedService>());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("59")]
    [InlineData("61")]
    public void Enabled_InvalidCadence_RejectsConfiguration(string interval)
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddDemoProducer(Config("true", interval)));
    }

    [Theory]
    [InlineData("60")]
    [InlineData(null)]
    public void Enabled_RegistersOneWorkerAndPreservesInjectedClock(string? interval)
    {
        var services = new ServiceCollection();
        var clock = new ProducerTestClock();
        services.AddSingleton<TimeProvider>(clock);
        services.AddLogging();
        services.AddDemoProducer(interval is null ? new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DemoProducer:Enabled"] = "true" }).Build() : Config("true", interval));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.Same(clock, provider.GetRequiredService<TimeProvider>());
        Assert.Single(provider.GetServices<IHostedService>());
    }

    [Fact]
    public async Task FirstCycle_WaitsExactBoundary_UsesFourFixedGroupsAndFreshScopes()
    {
        await using var rig = new ProducerTestRig();
        await rig.StartAsync();
        rig.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Empty(rig.Calls);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(60), await rig.Clock.NextDelayAsync());
        var calls = rig.Calls.ToArray();
        Assert.Equal(new[] { "Healthy", "HighLatency", "HighCpu", "Timeout" }, calls.Select(call => call.Request.Scenario));
        Assert.Equal(24, calls.SelectMany(call => call.Request.DeviceIds).Distinct().Count());
        Assert.Equal(FixedDeviceCatalog.Devices.Select(device => device.Id), calls.SelectMany(call => call.Request.DeviceIds));
        Assert.All(calls, call =>
        {
            Assert.Equal(6, call.Request.DeviceIds.Count);
            new TelemetryBatchIdValidator(rig.Clock).ValidateNewBatch(call.Request.BatchId);
        });
        Assert.Equal(4, calls.Select(call => call.Request.BatchId).Distinct().Count());
        Assert.Equal(4, calls.Select(call => call.Scope).Distinct().Count());
        Assert.Equal(8, rig.Scopes.Count);
        Assert.All(rig.Scopes, scope => Assert.True(scope.Disposed));
    }

    [Fact]
    public async Task Rotation_HoldsFourCycles_VisitsEveryScenarioAndRepeatsAfterSixteen()
    {
        await using var rig = new ProducerTestRig();
        await rig.StartAsync();
        string[] scenarios = ["Healthy", "HighLatency", "HighCpu", "Timeout"];
        for (int cycle = 0; cycle <= 16; cycle++)
        {
            await rig.CycleAsync();
            var calls = rig.Calls.ToArray().Skip(cycle * 4).ToArray();
            for (int group = 0; group < 4; group++)
            {
                Assert.Equal(scenarios[(group + cycle % 16 / 4) % 4], calls[group].Request.Scenario);
                Assert.Equal(FixedDeviceCatalog.Devices.Skip(group * 6).Take(6).Select(device => device.Id), calls[group].Request.DeviceIds);
            }
        }
    }

    [Fact]
    public async Task Eligibility_ExcludesRetiredOrMissingFixedDevicesAndUnrelatedRegistrations()
    {
        await using var rig = new ProducerTestRig();
        rig.Active.ExceptWith(FixedDeviceCatalog.Devices.Take(6).Select(device => device.Id));
        rig.Missing.Add(FixedDeviceCatalog.Devices[6].Id);
        await rig.StartAsync();
        await rig.CycleAsync();
        Assert.Equal(3, rig.Calls.Count);
        Assert.Equal(new[] { 5, 6, 6 }, rig.Calls.Select(call => call.Request.DeviceIds.Count));
        Assert.Equal(rig.Active.Except(rig.Missing).Order(), rig.Calls.SelectMany(call => call.Request.DeviceIds).Order());
    }

    [Fact]
    public async Task EmptyFleet_QueriesButNeverAttemptsIngestion()
    {
        await using var rig = new ProducerTestRig();
        rig.Active.Clear();
        await rig.StartAsync();
        await rig.CycleAsync();
        Assert.Empty(rig.Calls);
        Assert.Equal(4, rig.CatalogReads);
    }

    [Fact]
    public async Task SlowCycle_SkipsMissedTicksAndDoesNotOverlapOrDrift()
    {
        await using var rig = new ProducerTestRig();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Ingest = async (request, token) =>
        {
            if (request.DeviceIds.Contains(FirstId))
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return new(request.BatchId, request.DeviceIds.Count, rig.Clock.GetUtcNow(), false);
        };
        await rig.StartAsync();
        try
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(60));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            rig.Clock.Advance(TimeSpan.FromSeconds(190));
            Assert.Single(rig.Calls);
            release.TrySetResult();
            Assert.Equal(TimeSpan.FromSeconds(50), await rig.Clock.NextDelayAsync());
            Assert.Equal(4, rig.Calls.Count);
            rig.Clock.Advance(TimeSpan.FromSeconds(50));
            Assert.Equal(TimeSpan.FromSeconds(60), await rig.Clock.NextDelayAsync());
            Assert.Equal(8, rig.Calls.Count);
            Assert.Equal("HighLatency", rig.Calls.ToArray()[4].Request.Scenario); // Fifth boundary uses the current rotation.
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task WallClockCorrection_DoesNotChangeMonotonicCadence()
    {
        await using var rig = new ProducerTestRig();
        await rig.StartAsync();
        rig.Clock.AdjustUtc(TimeSpan.FromHours(-2));
        await rig.CycleAsync();
        Assert.Equal(4, rig.Calls.Count);
        Assert.All(rig.Calls, call => new TelemetryBatchIdValidator(rig.Clock).ValidateNewBatch(call.Request.BatchId));
    }

    [Fact]
    public async Task UncertainFailure_ReplaysSamePayloadBeforeCatalogAfterRetirement()
    {
        await using var rig = new ProducerTestRig();
        bool first = true;
        rig.Ingest = (request, _) =>
        {
            if (request.DeviceIds.Contains(FirstId) && first)
            {
                first = false;
                throw new InvalidOperationException("SENSITIVE PROVIDER DETAIL");
            }
            return Task.FromResult(new TelemetryIngestionResult(request.BatchId, request.DeviceIds.Count,
                rig.Clock.GetUtcNow(), request.DeviceIds.Contains(FirstId)));
        };
        await rig.StartAsync();
        await rig.CycleAsync();
        var original = rig.Calls.First();
        rig.Active.ExceptWith(original.Request.DeviceIds);
        await rig.CycleAsync();
        var replay = rig.Calls.ToArray()[4];
        Assert.Same(original.Request, replay.Request);
        Assert.NotSame(original.Scope, replay.Scope);
        Assert.True(original.Scope.Disposed);
        Assert.Equal(7, rig.CatalogReads);
        Assert.Contains(rig.Logs, log => log.Message.Contains(original.Request.BatchId.ToString()));
        Assert.All(rig.Logs, log => { Assert.Null(log.Error); Assert.DoesNotContain("SENSITIVE", log.Message); });
        await rig.CycleAsync();
        Assert.Equal(11, rig.Calls.Count); // Replayed work cleared; now-retired group is skipped.
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("ineligible")]
    public async Task ExplicitRejection_SkipsOpportunityThenCreatesCurrentObservationNextCycle(string rejection)
    {
        await using var rig = new ProducerTestRig();
        int attempts = 0;
        rig.Ingest = (request, _) =>
        {
            if (request.DeviceIds.Contains(FirstId))
            {
                attempts++;
                if (attempts <= 6) throw new InvalidOperationException("uncertain");
                if (attempts == 7)
                {
                    if (rejection == "expired") throw new TelemetryBatchIdValidationException("batch_id_expired", "expired");
                    throw new TelemetryDeviceEligibilityException();
                }
            }
            return Task.FromResult(new TelemetryIngestionResult(request.BatchId, request.DeviceIds.Count, rig.Clock.GetUtcNow(), false));
        };
        await rig.StartAsync();
        for (int cycle = 0; cycle < 7; cycle++) await rig.CycleAsync();
        var old = rig.Calls.First().Request;
        Assert.Equal(28, rig.Calls.Count); // No replacement in the rejection cycle.
        Assert.All(rig.Calls.Where(call => call.Request.DeviceIds.Contains(FirstId)), call => Assert.Same(old, call.Request));
        await rig.CycleAsync();
        var current = rig.Calls.ToArray()[28];
        Assert.NotEqual(old.BatchId, current.Request.BatchId);
        Assert.Equal("HighLatency", current.Request.Scenario);
        Assert.Equal(rig.Clock.GetUtcNow(), current.Time);
        new TelemetryBatchIdValidator(rig.Clock).ValidateNewBatch(current.Request.BatchId);
        Assert.Equal(26, rig.CatalogReads); // 4 initial + 6*3 unaffected groups + 4 current observations.
        Assert.Contains(rig.Logs, log => log.Message.Contains(old.BatchId.ToString()) && log.Message.Contains("prior commit may exist"));
    }

    [Theory]
    [InlineData("conflict")]
    [InlineData("batch_id_invalid")]
    [InlineData("batch_id_future")]
    public async Task InvariantFailure_SuspendsOnlyAffectedGroupAndPreservesIdentity(string code)
    {
        await using var rig = new ProducerTestRig();
        rig.Ingest = (request, _) =>
        {
            if (request.DeviceIds.Contains(FirstId))
            {
                if (code == "conflict") throw new TelemetryIngestionConflictException();
                throw new TelemetryBatchIdValidationException(code, "SENSITIVE");
            }
            return Task.FromResult(new TelemetryIngestionResult(request.BatchId, request.DeviceIds.Count, rig.Clock.GetUtcNow(), false));
        };
        await rig.StartAsync();
        await rig.CycleAsync();
        await rig.CycleAsync();
        Assert.Equal(7, rig.Calls.Count);
        var id = rig.Calls.First().Request.BatchId;
        Assert.Contains(rig.Logs, log => log.Message.Contains(id.ToString()) && log.Message.Contains("reconcile"));
        Assert.All(rig.Logs, log => Assert.DoesNotContain("SENSITIVE", log.Message));
    }

    [Fact]
    public async Task CatalogFailure_IsNotAnEmptyFleet_RecoversNextCycle()
    {
        await using var rig = new ProducerTestRig { CatalogError = new InvalidOperationException("SENSITIVE") };
        await rig.StartAsync();
        await rig.CycleAsync();
        Assert.Empty(rig.Calls);
        Assert.Equal(4, rig.Logs.Count);
        rig.CatalogError = null;
        await rig.CycleAsync();
        Assert.Equal(4, rig.Calls.Count);
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("cancelled_dependency")]
    public async Task NonterminalFailures_RetainOriginalRequestAndResume(string failure)
    {
        await using var rig = new ProducerTestRig();
        bool failing = true;
        rig.Ingest = (request, _) =>
        {
            if (failing && request.DeviceIds.Contains(FirstId))
            {
                if (failure == "busy") throw new TelemetryIngestionBusyException();
                throw new OperationCanceledException("Dependency cancellation without host shutdown");
            }
            return Task.FromResult(new TelemetryIngestionResult(request.BatchId, request.DeviceIds.Count, rig.Clock.GetUtcNow(), false));
        };
        await rig.StartAsync();
        await rig.CycleAsync();
        var original = rig.Calls.First().Request;
        failing = false;
        await rig.CycleAsync();
        Assert.Same(original, rig.Calls.ToArray()[4].Request);
        Assert.All(rig.Scopes, scope => Assert.True(scope.Disposed));
    }

    [Fact]
    public async Task Cancellation_DuringRecoveryDelay_DoesNotRetryOrLogRollback()
    {
        await using var rig = new ProducerTestRig();
        rig.Ingest = (_, _) => throw new InvalidOperationException("unknown commit");
        await rig.StartAsync();
        await rig.CycleAsync();
        Assert.Equal(4, rig.Calls.Count);
        await rig.Worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        rig.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(4, rig.Calls.Count);
        Assert.All(rig.Scopes, scope => Assert.True(scope.Disposed));
        Assert.All(rig.Logs, log => Assert.DoesNotContain("rollback", log.Message, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_StopsInitialDelayOrInflightAttemptAndDisposesScopes(bool inflight)
    {
        await using var rig = new ProducerTestRig();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Ingest = async (request, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(request.BatchId, 0, rig.Clock.GetUtcNow(), false);
        };
        await rig.StartAsync();
        if (inflight)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(60));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await rig.Worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(rig.Worker.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Equal(inflight ? 1 : 0, rig.Calls.Count);
        Assert.All(rig.Scopes, scope => Assert.True(scope.Disposed));
    }
}
