using System.Collections.Concurrent;
using HealthMonitor.Api.Telemetry;
using HealthMonitor.Application.Telemetry;
using HealthMonitor.UnitTests.Producer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HealthMonitor.UnitTests.Telemetry;

/// <summary>Runs the real retention worker and Application coordinator with scoped, database-free pruning outcomes.</summary>
internal sealed class RetentionWorkerTestRig : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private int _calls;
    private int _scopes;
    /// <summary>Existing explicitly advanced worker clock; no real hourly or backoff sleeps occur.</summary>
    internal ProducerTestClock Clock { get; } = new();
    /// <summary>Records each scope's lifecycle so retry and cancellation assertions cannot adopt a disposed store.</summary>
    internal ConcurrentQueue<StoreScope> Scopes { get; } = new();
    /// <summary>Global ordered store-call count, including multiple batches within one pass.</summary>
    internal int Calls => Volatile.Read(ref _calls);
    /// <summary>Test-owned store outcome; the default is a confirmed empty inspection batch.</summary>
    internal Func<int, CancellationToken, Task<TelemetryRetentionBatchResult>> Prune { get; set; } =
        (_, _) => Task.FromResult(new TelemetryRetentionBatchResult(0, 0, 0, false));
    /// <summary>Optional disposal fault/gate, invoked after the fake store is marked disposed.</summary>
    internal Func<int, Task>? Cleanup { get; set; }
    /// <summary>Captures structured confirmed counts separately from rendered messages and exception objects.</summary>
    internal ConcurrentQueue<(string Message, Exception? Error, int? Batches, int? Rows)> Logs { get; } = new();
    /// <summary>The actual worker, started directly without an HTTP host or database registration.</summary>
    internal TelemetryRetentionWorker Worker { get; }

    /// <summary>Builds a validating scoped container around the actual Application coordinator.</summary>
    internal RetentionWorkerTestRig()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddScoped<ITelemetryRetentionStore>(_ => new StoreScope(this));
        services.AddScoped<TelemetryRetentionService>();
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        Worker = new(_provider.GetRequiredService<IServiceScopeFactory>(), Clock, new CaptureLogger(this));
    }

    /// <summary>Waits for the next registered worker delay with the clock helper's independent five-second deadline.</summary>
    /// <param name="seconds">Expected delay after a complete attempt and scope disposal.</param>
    internal async Task ExpectDelayAsync(int seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), await Clock.NextDelayAsync());

    /// <summary>Stops the worker before disposing its container; stalled fake work fails within an outer deadline.</summary>
    public async ValueTask DisposeAsync()
    {
        await Worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        if (Worker.ExecuteTask is { } task) await task.WaitAsync(TimeSpan.FromSeconds(5));
        Worker.Dispose();
        await _provider.DisposeAsync();
    }

    /// <summary>Models per-pass store ownership and cleanup failure independently from pass completion.</summary>
    internal sealed class StoreScope : ITelemetryRetentionStore, IAsyncDisposable
    {
        private readonly RetentionWorkerTestRig _owner;
        private int _disposed;
        /// <summary>Stable identity of this pass scope.</summary>
        internal int Id { get; }
        /// <summary>Whether cleanup started; subsequent pruning is forbidden.</summary>
        internal bool Disposed => Volatile.Read(ref _disposed) != 0;
        /// <summary>Records a new pass-owned store before any work is invoked.</summary>
        internal StoreScope(RetentionWorkerTestRig owner)
        {
            _owner = owner;
            Id = Interlocked.Increment(ref owner._scopes);
            owner.Scopes.Enqueue(this);
        }
        /// <inheritdoc />
        public Task<TelemetryRetentionBatchResult> PruneBatchAsync(DateTimeOffset pingObservedBeforeUtc,
            DateTimeOffset historyBeforeUtc, CancellationToken cancellationToken = default)
        {
            Assert.False(Disposed);
            cancellationToken.ThrowIfCancellationRequested();
            return _owner.Prune(Interlocked.Increment(ref _owner._calls), cancellationToken);
        }
        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_owner.Cleanup is { } cleanup) await cleanup(Id);
        }
    }

    /// <summary>Captures structured logging to detect both secret leakage and incorrect confirmed-progress reporting.</summary>
    private sealed class CaptureLogger(RetentionWorkerTestRig owner) : ILogger<TelemetryRetentionWorker>
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var fields = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
            owner.Logs.Enqueue((formatter(state, exception), exception,
                (int?)fields.FirstOrDefault(pair => pair.Key == "Batches").Value,
                (int?)fields.FirstOrDefault(pair => pair.Key == "Rows").Value));
        }
    }
}
