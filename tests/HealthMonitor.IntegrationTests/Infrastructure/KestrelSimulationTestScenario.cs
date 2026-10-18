using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using HealthMonitor.Api.Composition;
using HealthMonitor.Api.Controllers;
using HealthMonitor.Application.Device;
using HealthMonitor.Application.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HealthMonitor.IntegrationTests.Infrastructure;

/// <summary>Serializes short-lived real listeners inside one test process.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SimulationEffectiveHostCollection
{
    /// <summary>Collection identity for effective-binding verification.</summary>
    public const string Name = "Simulation effective host";
}

/// <summary>Owns a real ephemeral Kestrel host with fake Application ports and actual listener/connection evidence.</summary>
/// <remarks>
/// Plain HostBuilder deliberately avoids Program, default application configuration, User Secrets, and runtime database registration.
/// No server-address or connection-IP feature is replaced. Wildcard negative tests briefly bind an ephemeral listener whose write gate denies access.
/// All HTTP/TCP clients target IPv4 loopback only. This is not hosted ingress, proxy, TLS, or real-ingestion proof.
/// </remarks>
internal sealed class KestrelSimulationTestScenario
{
    /// <summary>Configured/effective binding combinations; actual listener evidence is always read after startup.</summary>
    internal enum BindingMode { Loopback, WildcardOverride, LoopbackOverride }
    private readonly BindingMode mode;
    private readonly Exception? startupFailure;
    private IHost? host;
    private HttpClient? client;
    private CancellationToken applicationStopped;
    private bool ran;
    private readonly List<Exception> cleanupFailures = new();
    private readonly HashSet<int> observedPorts = new();
    internal string ConfiguredUrl { get; }
    internal string? ObservedConfiguredUrl { get; private set; }
    internal Uri? Address { get; private set; }
    internal IReadOnlyList<Uri> EffectiveBindings { get; private set; } = Array.Empty<Uri>();
    internal ConcurrentQueue<(IPAddress? Local, int LocalPort, IPAddress? Remote, int RemotePort)> Connections { get; } = new();
    internal SimulationTestHost.FakeIngestion Ingestion { get; } = new();
    internal HttpClient Client => client ?? throw new InvalidOperationException("Host is not ready.");
    internal bool ListenerClosed { get; private set; }
    internal bool Stopped => applicationStopped.IsCancellationRequested;
    internal bool StartupFailureObservedBoundPort { get; private set; }
    internal bool CleanupCapturedBindings { get; private set; }
    internal bool NoBoundListenerObserved { get; private set; }
    internal int PublisherCount { get; private set; }
    internal IReadOnlyList<Exception> CleanupFailures => cleanupFailures;

    /// <summary>Captures explicit binding intent; constructing the scenario opens no socket.</summary>
    /// <param name="mode">Configured URL/effective Kestrel override combination.</param>
    /// <param name="startupFailure">Optional sentinel thrown only after the real listener has positively bound.</param>
    internal KestrelSimulationTestScenario(BindingMode mode, Exception? startupFailure = null)
    {
        this.mode = mode; this.startupFailure = startupFailure;
        ConfiguredUrl = mode == BindingMode.LoopbackOverride ? "http://0.0.0.0:0" : "http://127.0.0.1:0";
    }

    /// <summary>Runs once and always quiesces startup/body/shutdown tasks before returning or rethrowing preserved failures.</summary>
    /// <param name="body">Assertions against the started feature host.</param>
    /// <returns>Completion after physical listener closure is verified.</returns>
    /// <exception cref="AggregateException">Both an original operation and cleanup failed.</exception>
    internal async Task RunAsync(Func<KestrelSimulationTestScenario, Task> body)
    {
        if (ran) throw new InvalidOperationException("Scenario runs only once."); ran = true;
        Exception? failure = null;
        try { await StartAsync(); await ObserveAsync(body(this)); }
        catch (Exception error) { failure = error; }
        try { await ObserveAsync(CleanupAsync()); }
        catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Starts only explicit feature services; the client never uses a proxy or follows a redirect outside the loopback target.</summary>
    private async Task StartAsync()
    {
        var values = new Dictionary<string, string?>
        {
            ["urls"] = ConfiguredUrl, ["Simulation:HttpEndpointEnabled"] = "true",
            ["Simulation:AllowLocalAnonymous"] = "true", ["Cors:AllowedOrigins:0"] = "http://localhost:5173"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var builder = new HostBuilder().UseEnvironment("Development")
            .ConfigureServices(services => services.Configure<HostOptions>(options =>
            { options.ServicesStartConcurrently = false; options.ShutdownTimeout = TimeSpan.FromSeconds(5); }));
        builder.ConfigureWebHost(web =>
        {
            web.UseKestrel().UseEnvironment("Development").UseSetting(WebHostDefaults.PreventHostingStartupKey, "true")
                .ConfigureAppConfiguration((_, settings) => { settings.Sources.Clear(); settings.AddInMemoryCollection(values); })
                .ConfigureLogging(logging => logging.ClearProviders().SetMinimumLevel(LogLevel.Information).AddProvider(new SafeReadLogs()))
                .ConfigureServices(services =>
                {
                    services.AddFleetReads(config);
                    var reads = new FakeReadPorts();
                    services.RemoveAll<IDeviceHealthQueries>(); services.RemoveAll<IDeviceHistoryQueries>(); services.RemoveAll<TimeProvider>();
                    services.AddSingleton<IDeviceCatalogQueries>(reads); services.AddSingleton<IDeviceHealthQueries>(reads);
                    services.AddSingleton<IDeviceHistoryQueries>(reads); services.AddSingleton<TimeProvider>(reads);
                    services.AddSingleton<ITelemetryIngestionService>(Ingestion);
                    services.AddReadApi(); services.AddReadCors(config); services.AddOperationalProbes();
                    services.AddSimulationApi(config, "Development");
                    // Shared controller discovery must retain the same disabled AI gate as production.
                    services.AddAiQueryApi(config);
                })
                .Configure(app =>
                {
                    // Observe actual socket endpoints without replacing their values or trusting Host/forwarded headers.
                    app.Use(async (context, next) =>
                    {
                        Connections.Enqueue((context.Connection.LocalIpAddress, context.Connection.LocalPort,
                            context.Connection.RemoteIpAddress, context.Connection.RemotePort));
                        await next(context);
                    });
                    app.UseReadApiErrors(); app.UseRouting(); app.UseCors(ReadCorsComposition.PolicyName);
                    app.UseLocalSimulationAccess(); app.UseRateLimiter(); app.UseAuthorization();
                    app.UseEndpoints(endpoints => { endpoints.MapControllers(); endpoints.MapOperationalProbes(); });
                });
            if (mode == BindingMode.WildcardOverride) web.ConfigureKestrel(options => options.ListenAnyIP(0));
            if (mode == BindingMode.LoopbackOverride) web.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        });
        // Root registration follows GenericWebHostService. The hook also verifies a positive actual port, so order is observed, not assumed.
        if (startupFailure is not null) builder.ConfigureServices(services => services.AddSingleton<IHostedService>(new FailAfterBind(this, startupFailure)));
        host = builder.Build();
        applicationStopped = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped;
        PublisherCount = host.Services.GetServices<IHealthCheckPublisher>().Count();
        var actions = host.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items;
        Assert.DoesNotContain(actions.OfType<ControllerActionDescriptor>(), action => action.ControllerTypeInfo.AsType() == typeof(AiDeviceQueriesController));
        using var startBudget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await ObserveAsync(host.StartAsync(startBudget.Token));
        CaptureBindings();
        client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { BaseAddress = Address, Timeout = TimeSpan.FromSeconds(5) };
    }

    /// <summary>Reads all effective addresses only after binding; configured URLs remain separate evidence.</summary>
    private void CaptureBindings()
    {
        if (host is null) throw new InvalidOperationException("Host was not built.");
        ObservedConfiguredUrl = host.Services.GetRequiredService<IConfiguration>()["urls"];
        var actual = RawBindings().Select(value => new Uri(value, UriKind.Absolute)).ToArray();
        foreach (var binding in actual.Where(value => value.Port > 0)) observedPorts.Add(binding.Port);
        if (actual.Length == 0 || actual.Any(value => value.Scheme != "http" || value.Port <= 0)
            || actual.Select(value => value.Port).Distinct().Count() != 1)
            throw new InvalidOperationException("A single actual ephemeral listener port must be observed.");
        EffectiveBindings = Array.AsReadOnly(actual);
        Address = new Uri($"http://127.0.0.1:{actual[0].Port}");
    }

    /// <summary>Reads the current server feature without using configured URLs as listener evidence.</summary>
    private string[] RawBindings() => host?.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.ToArray()
        ?? Array.Empty<string>();

    /// <summary>Recovers every observed positive listener port when startup failed before normal address capture.</summary>
    /// <remarks>No positive port means no observed listener, not proof of closure. Capture faults are retained and never bypass stop/dispose.</remarks>
    private void CaptureForCleanupIfNeeded()
    {
        if (Address is not null) return;
        try
        {
            var positive = new List<Uri>();
            bool malformed = false;
            foreach (string raw in RawBindings())
            {
                if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                {
                    malformed = true; cleanupFailures.Add(new InvalidOperationException("Unexpected effective listener evidence.")); continue;
                }
                if (uri.Port > 0) { positive.Add(uri); observedPorts.Add(uri.Port); }
            }
            if (positive.Count > 0)
            {
                EffectiveBindings = positive.AsReadOnly(); Address = new Uri($"http://127.0.0.1:{positive[0].Port}");
                CleanupCapturedBindings = true;
            }
            else NoBoundListenerObserved = !malformed;
        }
        catch (Exception error) { cleanupFailures.Add(error); }
    }

    /// <summary>Uses fresh cleanup cancellation after failed/canceled startup; never treats the drain helper's swallowed faults as success.</summary>
    private async Task CleanupAsync()
    {
        // Continue server cleanup even if disposing the client unexpectedly fails.
        try { client?.Dispose(); } catch (Exception error) { cleanupFailures.Add(error); }
        if (host is not null)
        {
            CaptureForCleanupIfNeeded();
            using var stopBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await ObserveAsync(host.StopAsync(stopBudget.Token)); } catch (Exception error) { cleanupFailures.Add(error); }
            try { await ObserveAsync(((IAsyncDisposable)host).DisposeAsync().AsTask()); } catch (Exception error) { cleanupFailures.Add(error); }
            if (!Stopped) cleanupFailures.Add(new InvalidOperationException("ApplicationStopped was not observed."));
        }
        if (Address is not null)
            try { await VerifyClosedAsync(); } catch (Exception error) { cleanupFailures.Add(error); }
        if (cleanupFailures.Count > 0) throw new AggregateException("Effective-host cleanup failed.", cleanupFailures);
    }

    /// <summary>Requires an actual refused new TCP connection; timeout, cancellation, or another socket error is inconclusive and fails.</summary>
    /// <returns>Completion only after ConnectionRefused from the independently created loopback client.</returns>
    internal async Task VerifyClosedAsync()
    {
        if (Address is null || observedPorts.Count == 0) throw new InvalidOperationException("No positively observed listener is available for closure verification.");
        foreach (int port in observedPorts)
        {
            using var tcp = new TcpClient(AddressFamily.InterNetwork);
            // Windows can report a refused loopback connect after more than two seconds; the bound is not itself closure evidence.
            using var probeBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await tcp.ConnectAsync(IPAddress.Loopback, port, probeBudget.Token); }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionRefused) { continue; }
            throw new InvalidOperationException("The listener still accepted a new connection.");
        }
        ListenerClosed = true;
    }

    /// <summary>Fail-stops on unresolved owned tasks, then explicitly propagates completed task faults instead of swallowing them.</summary>
    private static async Task ObserveAsync(Task work)
    {
        await TelemetryIngestionTestScenario.DrainWritersAsync(new[] { work });
        await work;
    }

    /// <summary>Injects a startup failure only after observing real binding; this hook performs no background work.</summary>
    /// <param name="owner">Scenario retaining actual address/cleanup evidence.</param>
    /// <param name="failure">Original sentinel whose identity must survive shutdown.</param>
    private sealed class FailAfterBind(KestrelSimulationTestScenario owner, Exception failure) : IHostedService
    {
        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Prove post-bind ordering without initializing Address: cleanup must exercise its failed-start fallback.
            var actual = owner.RawBindings().Select(value => new Uri(value, UriKind.Absolute)).ToArray();
            owner.StartupFailureObservedBoundPort = actual.Length > 0 && actual.All(value => value.Port > 0);
            if (!owner.StartupFailureObservedBoundPort) throw new InvalidOperationException("Failure injection preceded actual binding.");
            throw failure;
        }
        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
