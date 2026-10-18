using System.Collections.Concurrent;
using HealthMonitor.Api.Composition;
using HealthMonitor.Api.Controllers;
using HealthMonitor.Application.Device;
using HealthMonitor.Application.IntegrationContracts;
using HealthMonitor.Application.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace HealthMonitor.IntegrationTests.Infrastructure;

/// <summary>Runs the shared read feature with WebApplicationFactory/TestServer, using explicit fake ports or fixture-verified database services.</summary>
/// <remarks>Never executes Program, loads application secrets/runtime settings, or starts application producer/retention workers. Not full startup, Kestrel, HTTPS, or deployment proof.</remarks>
internal sealed class ReadApiTestHost : IAsyncDisposable
{
    private readonly FeatureFactory factory;
    private readonly RequestCompletion requests;
    /// <summary>Owned TestServer client; each request has a ten-second outer timeout.</summary>
    internal HttpClient Client { get; }
    /// <summary>Admitted formatted diagnostics, including attached exceptions if a pipeline leaks one.</summary>
    internal SafeReadLogs Logs { get; }
    /// <summary>Feature-only services for explicit diagnostic policy assertions.</summary>
    internal IServiceProvider Services => factory.Services;

    /// <summary>Starts the explicit feature WebHost without invoking the API entrypoint.</summary>
    private ReadApiTestHost(Action<IServiceCollection> configure, bool providerSpecificBroadRule, IConfiguration simulationConfiguration, string environmentName)
    {
        Logs = new SafeReadLogs(); requests = new RequestCompletion();
        factory = new FeatureFactory(configure, Logs, requests, providerSpecificBroadRule, simulationConfiguration, environmentName);
        try
        {
            Client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            Client.Timeout = TimeSpan.FromSeconds(10);
        }
        catch { factory.Dispose(); throw; }
    }

    /// <summary>Registers only controlled fake ports, keeping local HTTP tests entirely database-free.</summary>
    /// <param name="ports">Controlled application facts and clock.</param>
    /// <returns>Started caller-owned host.</returns>
    internal static Task<ReadApiTestHost> StartAsync(FakeReadPorts ports) => StartAsync(services =>
    {
        services.RemoveAll<IDeviceHealthQueries>(); services.RemoveAll<IDeviceHistoryQueries>(); services.RemoveAll<TimeProvider>();
        services.AddSingleton<IDeviceCatalogQueries>(ports); services.AddSingleton<IDeviceHealthQueries>(ports);
        services.AddSingleton<IDeviceHistoryQueries>(ports); services.AddSingleton<TimeProvider>(ports);
    }, ports.ProviderSpecificBroadRule);

    /// <summary>Builds only the shared read feature with explicit services, never application database configuration.</summary>
    /// <param name="configure">Fake services or contexts on an independently fixture-verified disposable source.</param>
    /// <param name="providerSpecificBroadRule">Whether a diagnostic test deliberately injects a broad conflicting rule.</param>
    /// <param name="simulationConfiguration">Explicit nonsecret simulator/CORS settings only; omission keeps simulation disabled.</param>
    /// <param name="environmentName">Explicit test environment, never read from application settings or secrets.</param>
    /// <returns>Started caller-owned host; external sources/connections/transactions remain caller-owned.</returns>
    internal static Task<ReadApiTestHost> StartAsync(Action<IServiceCollection> configure, bool providerSpecificBroadRule = false,
        IConfiguration? simulationConfiguration = null, string environmentName = "Testing") =>
        Task.FromResult(new ReadApiTestHost(configure, providerSpecificBroadRule,
            simulationConfiguration ?? new ConfigurationBuilder().Build(), environmentName));

    /// <summary>Stops client admission, independently drains server execution, then stops/disposes the factory before external database cleanup.</summary>
    /// <remarks>Client cancellation alone is not proof that a query stopped. Unresponsive server execution triggers the existing fail-stop deadline.</remarks>
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await requests.StopAndDrainAsync();
        await factory.DisposeAsync();
    }

    /// <summary>Owns server completion barriers separately from possibly canceled HttpClient tasks.</summary>
    private sealed class RequestCompletion
    {
        private readonly object gate = new();
        private readonly List<Task> work = new();
        private bool closed;

        /// <summary>Admits a pipeline before database work; late requests after shutdown cannot enter the feature.</summary>
        internal TaskCompletionSource? Enter()
        {
            lock (gate)
            {
                if (closed) return null;
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                work.Add(done.Task); return done;
            }
        }
        /// <summary>Closes admission atomically and waits for every admitted server pipeline before any source/transaction can be disposed.</summary>
        internal Task StopAndDrainAsync()
        {
            Task[] pending;
            lock (gate) { closed = true; pending = work.ToArray(); }
            return TelemetryIngestionTestScenario.DrainWritersAsync(pending);
        }
    }

    /// <summary>Overrides factory host creation so Program, runtime credential selection, and application producer/retention workers cannot run.</summary>
    /// <param name="configure">Explicit read service overrides.</param>
    /// <param name="logs">Diagnostic capture after production filtering.</param>
    /// <param name="requests">Independent server-completion barriers.</param>
    /// <param name="broadRule">Whether the logger-rule regression condition is enabled.</param>
    /// <param name="simulationConfiguration">Explicit nonsecret test flags and origins.</param>
    /// <param name="environmentName">Explicit test environment used by both host builders.</param>
    private sealed class FeatureFactory(Action<IServiceCollection> configure, SafeReadLogs logs, RequestCompletion requests, bool broadRule,
        IConfiguration simulationConfiguration, string environmentName)
        : WebApplicationFactory<global::Program>
    {
        /// <inheritdoc />
        protected override IHostBuilder CreateHostBuilder() => new HostBuilder()
            .UseEnvironment(environmentName)
            .ConfigureWebHost(web => web.UseEnvironment(environmentName)
                .ConfigureAppConfiguration((_, configuration) => configuration.Sources.Clear())
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders().SetMinimumLevel(LogLevel.Information).AddFilter("Microsoft.AspNetCore", LogLevel.Warning)
                        .AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Trace)
                        .AddFilter("Npgsql.Command", LogLevel.Trace).AddProvider(logs);
                    if (broadRule) logging.AddFilter<SafeReadLogs>(null, LogLevel.Trace);
                })
                .ConfigureServices(services =>
                {
                    services.AddFleetReads(new ConfigurationBuilder().Build());
                    services.AddReadCors(simulationConfiguration);
                    services.AddOperationalProbes();
                    configure(services);
                    services.AddReadApi();
                    services.AddSimulationApi(simulationConfiguration, environmentName);
                })
                .Configure(app =>
                {
                    app.Use(async (context, next) =>
                    {
                        var done = requests.Enter();
                        if (done is null) { context.Response.StatusCode = 503; return; }
                        try { await next(); }
                        finally { done.TrySetResult(); }
                    });
                    app.UseReadApiErrors(); app.UseRouting();
                    app.UseCors(ReadCorsComposition.PolicyName);
                    app.UseLocalSimulationAccess(); app.UseRateLimiter();
                    app.UseEndpoints(endpoints => { endpoints.MapControllers(); endpoints.MapOperationalProbes(); });
                }));
    }
}

/// <summary>Captures formatted logs and attached exception messages to detect leakage, not merely the owned handler template.</summary>
internal sealed class SafeReadLogs : ILoggerProvider
{
    internal ConcurrentQueue<string> Entries { get; } = new();
    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);
    /// <inheritdoc />
    public void Dispose() { }

    /// <summary>Captures only levels admitted by the host's explicit production-style filters.</summary>
    private sealed class Capture(SafeReadLogs owner, string category) : ILogger
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Entries.Enqueue(category + ": " + formatter(state, exception) + (exception is null ? "" : " " + exception.ToString()));
    }
}

/// <summary>Supplies immutable facts through actual application ports; it deliberately performs no persistence or external calls.</summary>
/// <remarks>Fake history implements the port contract only to test transport/composition. PostgreSQL behavior is a separate B2 gate.</remarks>
internal sealed class FakeReadPorts : TimeProvider, IDeviceCatalogQueries, IDeviceHealthQueries, IDeviceHistoryQueries
{
    internal DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    internal List<DeviceRegistration> Devices { get; } = new();
    internal List<DeviceTelemetrySnapshot> States { get; } = new();
    internal List<TelemetryHistoryItem> Pings { get; } = new();
    internal List<IncidentHistoryItem> Incidents { get; } = new();
    internal int CatalogCalls, TelemetryCalls, HistoryCalls, ClockReads;
    internal TelemetryHistoryRange? LastRange;
    internal IncidentHistoryState LastState;
    internal Exception? Failure;
    internal bool ProviderSpecificBroadRule;
    internal Func<CancellationToken, Task>? OnRead;

    /// <summary>Counts real composition clock calls against a fixed instant.</summary>
    public override DateTimeOffset GetUtcNow() { Interlocked.Increment(ref ClockReads); return Now; }

    /// <summary>Adds controlled metadata without constructing a domain entity or provisioning inventory.</summary>
    /// <param name="number">Deterministic local identity suffix.</param>
    /// <param name="retired">Whether detail should preserve retirement.</param>
    /// <returns>The added registration.</returns>
    internal DeviceRegistration AddDevice(int number, bool retired = false)
    {
        var device = new DeviceRegistration(Guid.Parse($"00000000-0000-0000-0000-{number:D12}"),
            $"Device {number:D2}", $"TAG-{number:D2}", "DEN", "Server", retired);
        Devices.Add(device);
        return device;
    }

    /// <summary>Injects controlled failures/cancellation at the application port, never by contacting a server.</summary>
    private async Task BeforeAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Failure is not null) throw Failure;
        if (OnRead is not null) await OnRead(token);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceRegistration>> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref CatalogCalls); await BeforeAsync(cancellationToken);
        return Devices.Where(d => !d.IsRetired).ToArray();
    }
    /// <inheritdoc />
    public async Task<DeviceCatalogDetail?> GetDetailAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref CatalogCalls); await BeforeAsync(cancellationToken);
        var device = Devices.SingleOrDefault(d => d.DeviceId == deviceId);
        return device is null ? null : new(device, Now.AddDays(-2), device.IsRetired ? Now.AddDays(-1) : null);
    }
    /// <inheritdoc />
    public Task<IReadOnlyList<DeviceRegistration>> GetByIdsAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Read routes must use the Device application query operations.");
    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceTelemetrySnapshot>> GetLatestByDeviceIdsAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref TelemetryCalls); await BeforeAsync(cancellationToken);
        return States.Where(s => deviceIds.Contains(s.DeviceId)).ToArray();
    }
    /// <inheritdoc />
    public async Task<HistoryPage<TelemetryHistoryItem>> GetTelemetryAsync(Guid deviceId, HistoryPaging paging,
        TelemetryHistoryRange range, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref HistoryCalls); await BeforeAsync(cancellationToken); LastRange = range;
        var rows = Pings.Where(p => p.DeviceId == deviceId && p.ObservedAtUtc >= range.FromUtc && p.ObservedAtUtc < range.ToUtc)
            .OrderByDescending(p => p.ObservedAtUtc).ThenByDescending(p => p.Id).ToArray();
        return Slice(rows, paging);
    }
    /// <inheritdoc />
    public async Task<HistoryPage<IncidentHistoryItem>> GetIncidentsAsync(Guid deviceId, HistoryPaging paging,
        IncidentHistoryState state = IncidentHistoryState.All, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref HistoryCalls); await BeforeAsync(cancellationToken); LastState = state;
        var rows = Incidents.Where(i => i.DeviceId == deviceId && (state == IncidentHistoryState.All ||
            (state == IncidentHistoryState.Open && i.ResolvedAtUtc is null) || (state == IncidentHistoryState.Resolved && i.ResolvedAtUtc is not null)))
            .OrderByDescending(i => i.OpenedAtUtc).ThenByDescending(i => i.Id).ToArray();
        return Slice(rows, paging);
    }

    /// <summary>Creates a fake page without narrowing an out-of-range long offset; production SQL is tested separately.</summary>
    private static HistoryPage<T> Slice<T>(T[] rows, HistoryPaging paging) => new(
        Array.AsReadOnly(paging.Offset >= rows.Length ? Array.Empty<T>() : rows.Skip((int)paging.Offset).Take(paging.PageSize).ToArray()),
        paging, rows.LongLength);
}
