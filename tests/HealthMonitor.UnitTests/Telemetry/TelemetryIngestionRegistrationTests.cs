using HealthMonitor.Api.Composition;
using HealthMonitor.Application.IntegrationContracts;
using HealthMonitor.Application.Telemetry;
using HealthMonitor.Domain.Telemetry;
using HealthMonitor.Infrastructure.Data.DeviceContext;
using HealthMonitor.Infrastructure.Data.TelemetryContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HealthMonitor.UnitTests.Telemetry;

/// <summary>Checks scoped ingestion composition and pre-admission guards without opening database connections.</summary>
public sealed class TelemetryIngestionRegistrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Supplies a deterministic server clock for preparing validated transaction inputs.</summary>
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Captures an actual Application-produced handoff without making its constructor public for tests.</summary>
    private sealed class Capture : ISimulationBatchRepository, IDeviceCatalog, ITelemetryIngestionTransaction
    {
        internal TelemetryIngestionBatch? Batch { get; private set; }
        public Task<SimulationBatch?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<SimulationBatch?>(null);
        public void Add(SimulationBatch receipt) => throw new InvalidOperationException("No persistence in capture helper.");
        public Task<IReadOnlyList<DeviceRegistration>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeviceRegistration>>(ids.Select(id => new DeviceRegistration(id, "Test", "TEST", "DEN", "Server", false)).ToArray());
        public Task<TelemetryIngestionResult> ExecuteAsync(TelemetryIngestionBatch batch, CancellationToken cancellationToken = default)
        {
            Batch = batch;
            return Task.FromResult(new TelemetryIngestionResult(batch.BatchId, batch.DeviceIds.Count, Now, false));
        }
    }

    /// <summary>Obtains stable inputs through the real validation path, without persistence or a test-only production hook.</summary>
    private static async Task<TelemetryIngestionBatch> BatchAsync()
    {
        var capture = new Capture();
        await new TelemetryIngestionService(capture, capture, capture, new Clock()).IngestAsync(
            new(Guid.CreateVersion7(Now), "Healthy", new[] { Guid.NewGuid() }));
        return capture.Batch!;
    }

    /// <summary>Builds a context whose provider is configured but whose connection must never be opened by guards.</summary>
    private static TelemetryDbContext Context() => new(new DbContextOptionsBuilder<TelemetryDbContext>()
        .UseNpgsql("Host=example.invalid;Database=fictional;Username=unused").Options);

    private static TelemetryIngestionTransaction Adapter(TelemetryDbContext context) => new(
        context, new SimulationBatchRepository(context), new IncidentLifecycleProcessor(), new Clock(),
        NullLogger<TelemetryIngestionTransaction>.Instance);

    [Fact]
    public async Task Registration_UsesScopedServicesAndPreservesClockAndContexts()
    {
        var services = new ServiceCollection();
        var clock = new Clock();
        services.AddSingleton<TimeProvider>(clock);
        services.AddLogging();
        services.AddDbContext<TelemetryDbContext>(options => options.UseNpgsql("Host=example.invalid;Database=fictional;Username=unused"));
        services.AddDbContext<DeviceDbContext>();
        services.AddDeviceCatalog();
        Assert.Same(services, services.AddTelemetryIngestion());
        foreach (var type in new[] { typeof(ITelemetryIngestionService), typeof(ITelemetryIngestionTransaction), typeof(ISimulationBatchRepository) })
            Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, descriptor => descriptor.ServiceType == type).Lifetime);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(TelemetryDbContext));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(TimeProvider));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        Assert.Same(clock, provider.GetRequiredService<TimeProvider>());
        Assert.Same(first.ServiceProvider.GetRequiredService<ITelemetryIngestionService>(), first.ServiceProvider.GetRequiredService<ITelemetryIngestionService>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<ITelemetryIngestionTransaction>(), second.ServiceProvider.GetRequiredService<ITelemetryIngestionTransaction>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<ISimulationBatchRepository>(), second.ServiceProvider.GetRequiredService<ISimulationBatchRepository>());
        // Staging through the resolved repository must appear in the resolved context. The
        // resolved adapter must then reject that exact tracked work before opening a connection.
        var context = first.ServiceProvider.GetRequiredService<TelemetryDbContext>();
        var receipt = SimulationBatch.CreateCommitted(Guid.NewGuid(), new string('a', 64), "Healthy", Now, Now, 1);
        first.ServiceProvider.GetRequiredService<ISimulationBatchRepository>().Add(receipt);
        Assert.Same(receipt, Assert.Single(context.ChangeTracker.Entries()).Entity);
        var batch = await BatchAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => first.ServiceProvider
            .GetRequiredService<ITelemetryIngestionTransaction>().ExecuteAsync(batch));
        Assert.Equal("Ingestion requires an exclusive context without existing transactions or tracking.", error.Message);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task Adapter_TrackedCallerWork_IsRejectedWithoutDisposalOrDetachment()
    {
        await using var context = Context();
        var receipt = SimulationBatch.CreateCommitted(Guid.NewGuid(), new string('a', 64), "Healthy", Now, Now, 1);
        context.SimulationBatches.Add(receipt);
        var batch = await BatchAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Adapter(context).ExecuteAsync(batch));
        Assert.Same(receipt, Assert.Single(context.ChangeTracker.Entries()).Entity);
        Assert.Equal(EntityState.Added, context.Entry(receipt).State);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task Adapter_AmbientTransaction_IsRejectedBeforeOpeningConnection()
    {
        await using var context = Context();
        using var ambient = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
        var batch = await BatchAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Adapter(context).ExecuteAsync(batch));
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task Adapter_NullBatch_IsRejectedBeforeOpeningConnection()
    {
        await using var context = Context();
        await Assert.ThrowsAsync<ArgumentNullException>(() => Adapter(context).ExecuteAsync(null!));
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task Adapter_PreCanceledToken_IsRejectedBeforeOpeningConnection()
    {
        await using var context = Context();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var batch = await BatchAsync();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Adapter(context).ExecuteAsync(batch, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }
}
