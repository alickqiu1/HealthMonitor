using System.Collections.Concurrent;
using HealthMonitor.Api.Provisioning;
using HealthMonitor.Api.Telemetry;
using HealthMonitor.Application.Device;
using HealthMonitor.Application.IntegrationContracts;
using HealthMonitor.Application.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HealthMonitor.UnitTests.Producer;

/// <summary>Runs the real worker against scoped Application fakes; it never creates a host or database service.</summary>
internal sealed class ProducerTestRig : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    /// <summary>The controlled UTC and scheduling clock.</summary>
    internal ProducerTestClock Clock { get; } = new();
    /// <summary>Fixed registration eligibility, changed only between completed test cycles.</summary>
    internal HashSet<Guid> Active { get; } = FixedDeviceCatalog.Devices.Select(device => device.Id).ToHashSet();
    /// <summary>Fixed IDs omitted by the lookup, separately from registrations returned as retired.</summary>
    internal HashSet<Guid> Missing { get; } = new();
    /// <summary>Captured attempts with identity, immutable payload, attempt scope, and current UTC.</summary>
    internal ConcurrentQueue<(TelemetryIngestionRequest Request, Scope Scope, DateTimeOffset Time)> Calls { get; } = new();
    /// <summary>Scopes include catalog reads and every independent write attempt.</summary>
    internal ConcurrentQueue<Scope> Scopes { get; } = new();
    /// <summary>Sanitized log messages and any accidentally attached exception.</summary>
    internal ConcurrentQueue<(string Message, Exception? Error)> Logs { get; } = new();
    /// <summary>Optional ingestion fault/gate; defaults to a successful result.</summary>
    internal Func<TelemetryIngestionRequest, CancellationToken, Task<TelemetryIngestionResult>>? Ingest { get; set; }
    /// <summary>Optional catalog fault, preserving the distinction between failed reads and empty inventory.</summary>
    internal Exception? CatalogError { get; set; }
    /// <summary>Number of new-opportunity catalog queries, allowing replay-before-filter assertions.</summary>
    internal int CatalogReads;
    /// <summary>The actual BackgroundService, started directly without starting an application.</summary>
    internal DemoTelemetryProducer Worker { get; }

    /// <summary>Builds validating DI with scoped fakes and the actual producer constructor.</summary>
    internal ProducerTestRig()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new Scope(this));
        services.AddScoped<IDeviceCatalogQueries>(provider => provider.GetRequiredService<Scope>());
        services.AddScoped<ITelemetryIngestionService>(provider => provider.GetRequiredService<Scope>());
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        Worker = new(_provider.GetRequiredService<IServiceScopeFactory>(), Clock, new CapturingLogger(this));
    }

    /// <summary>Starts only the worker and synchronizes with its first delay.</summary>
    internal async Task StartAsync()
    {
        await Worker.StartAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(60), await Clock.NextDelayAsync());
    }

    /// <summary>Advances one minute and waits for all groups to finish before returning.</summary>
    internal async Task CycleAsync()
    {
        Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(TimeSpan.FromSeconds(60), await Clock.NextDelayAsync());
    }

    /// <summary>Stops with an outer deadline, then disposes scoped resources; stalled workers fail rather than hang.</summary>
    public async ValueTask DisposeAsync()
    {
        await Worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        if (Worker.ExecuteTask is { } task) await task.WaitAsync(TimeSpan.FromSeconds(5));
        Worker.Dispose();
        await _provider.DisposeAsync();
    }

    /// <summary>Records scope ownership and supplies no persistence implementation.</summary>
    internal sealed class Scope : IDeviceCatalogQueries, ITelemetryIngestionService, IDisposable
    {
        private readonly ProducerTestRig _owner;
        /// <summary>Whether the producer disposed this attempt before continuing.</summary>
        internal bool Disposed { get; private set; }
        /// <summary>Registers this scope for ownership assertions.</summary>
        internal Scope(ProducerTestRig owner) { _owner = owner; owner.Scopes.Enqueue(this); }
        /// <inheritdoc />
        public Task<IReadOnlyList<DeviceRegistration>> GetByIdsAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _owner.CatalogReads);
            if (_owner.CatalogError is { } error) throw error;
            // Include an unrelated registration deliberately: production must intersect with fixed membership.
            return Task.FromResult<IReadOnlyList<DeviceRegistration>>(deviceIds.Where(id => !_owner.Missing.Contains(id)).Select(id =>
                new DeviceRegistration(id, "Test", "TEST", "DEN", "Server", !_owner.Active.Contains(id)))
                .Append(new(Guid.NewGuid(), "Other", "OTHER", "DEN", "Server", false)).ToArray());
        }
        /// <inheritdoc />
        public Task<IReadOnlyList<DeviceRegistration>> GetActiveAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The producer must select only its fixed fleet.");
        /// <inheritdoc />
        public Task<TelemetryIngestionResult> IngestAsync(TelemetryIngestionRequest request, CancellationToken cancellationToken = default)
        {
            Assert.False(Disposed);
            cancellationToken.ThrowIfCancellationRequested();
            _owner.Calls.Enqueue((request, this, _owner.Clock.GetUtcNow()));
            return _owner.Ingest?.Invoke(request, cancellationToken) ?? Task.FromResult(
                new TelemetryIngestionResult(request.BatchId, request.DeviceIds.Count, _owner.Clock.GetUtcNow(), false));
        }
        /// <inheritdoc />
        public void Dispose() => Disposed = true;
    }

    /// <summary>Captures formatted output to ensure exception messages never leak through the worker's logger.</summary>
    private sealed class CapturingLogger(ProducerTestRig owner) : ILogger<DemoTelemetryProducer>
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Logs.Enqueue((formatter(state, exception), exception));
    }
}
