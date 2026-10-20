using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using HealthMonitor.Api.AiQueries;
using HealthMonitor.Api.Composition;
using HealthMonitor.Application.AiQueries;
using HealthMonitor.Application.AiQueries.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HealthMonitor.IntegrationTests.Infrastructure;

/// <summary>Serializes the bounded local ingress listeners within this test process.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AiIngressEffectiveHostCollection
{
    /// <summary>Identifies the real-connection ingress acceptance group.</summary>
    public const string Name = "AI ingress effective host";
}

/// <summary>Owns ephemeral loopback Kestrel, a seeded private test counter and fake generation without running Program.</summary>
/// <remarks>Uses actual connection capture and request middleware; never fabricates a verified identity feature.
/// Explicit test options bypass only the still-blocked production activation composition. No application settings,
/// User Secrets, database, TLS, external proxy or real provider are loaded. This is component acceptance only.</remarks>
internal sealed class KestrelAiIngressTestScenario
{
    /// <summary>Records actual request facts after the production ingress middleware, before admission.</summary>
    internal sealed record Observation(string Connection, int? Stream, string Protocol, string? RawPeer, string? Client, string Scheme);
    private readonly string root = Path.Combine(Path.GetTempPath(), "HealthMonitor-ai-ingress-" + Guid.NewGuid().ToString("N"));
    private readonly List<Task> completions = new();
    private readonly object sync = new();
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource overlapping = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IHost? host;
    private HttpClient? client;
    private AiDailyBudgetStore? budget;
    private AiAdmissionPolicy? policy;
    private bool accepting = true;
    private int calls;
    private bool ran;
    internal ConcurrentQueue<Observation> Observations { get; } = new();
    internal HttpClient Client => client ?? throw new InvalidOperationException("Listener is not ready.");
    internal Uri? Address { get; private set; }
    internal bool ListenerClosed { get; private set; }
    internal bool NoBoundListenerObserved { get; private set; }
    internal int Calls => Volatile.Read(ref calls);
    internal Task BothProvidersStarted => overlapping.Task;
    internal bool Http2 { get; init; }
    internal bool HoldProviders { get; init; }
    internal string? KnownProxy { get; init; }
    internal bool OmitCapture { get; init; }
    internal bool RewritePeer { get; init; }
    internal string? AutomaticForwarding { get; init; }
    internal Exception? StartupFailure { get; init; }
    private string CounterPath => Path.Combine(root, "counter.json");
    private readonly QueryVocabulary vocabulary = new(["DEN"], ["Server"]);

    /// <summary>Runs once with explicit deadlines and retains both operation and cleanup failures.</summary>
    /// <param name="body">Assertions against the actual started listener.</param>
    /// <returns>Completion only after server work and owned resources have terminated safely.</returns>
    internal Task RunAsync(Func<KestrelAiIngressTestScenario, Task> body) =>
        ObserveAsync(RunCoreAsync(body), TimeSpan.FromSeconds(75));

    /// <summary>Preserves the original operation while cleanup has separate fresh bounded phases.</summary>
    private async Task RunCoreAsync(Func<KestrelAiIngressTestScenario, Task> body)
    {
        if (ran) throw new InvalidOperationException("Scenario cannot be reused.");
        ran = true;
        Exception? failure = null;
        try
        {
            await ObserveAsync(StartAsync(), TimeSpan.FromSeconds(15));
            await ObserveAsync(body(this), TimeSpan.FromSeconds(20));
        }
        catch (Exception error) { failure = error; }
        try { await CleanupAsync(); }
        catch (Exception cleanup)
        {
            if (failure is not null) throw new AggregateException("Ingress operation and cleanup failed.", failure, cleanup);
            throw;
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Releases controlled provider overlap even when a test assertion failed.</summary>
    internal void ReleaseProviders() => release.TrySetResult();

    /// <summary>Reads persisted reservations independently of HTTP responses and provider counts.</summary>
    internal int Count()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(CounterPath));
        return document.RootElement.GetProperty("count").GetInt32();
    }

    /// <summary>Starts only explicit production ingress components around a fake-provider admission endpoint.</summary>
    private async Task StartAsync()
    {
        Directory.CreateDirectory(Path.Combine(root, "host"));
        File.WriteAllText(CounterPath, "{\"version\":1,\"day\":\"2026-10-19\",\"count\":0}");
        var clock = new Clock();
        budget = new(CounterPath, clock); policy = new();
        var values = new Dictionary<string, string?>
        {
            ["AiQueries:Enabled"] = "true", ["AiQueries:ApiKey"] = "synthetic-test-key",
            ["AiQueries:BudgetCounterPath"] = CounterPath
        };
        if (KnownProxy is not null)
        {
            values["TrustedIngress:Enabled"] = "true"; values["TrustedIngress:ForwardLimit"] = "1";
            values["TrustedIngress:KnownProxies:0"] = KnownProxy;
        }
        if (AutomaticForwarding is not null) values["ForwardedHeaders_Enabled"] = AutomaticForwarding;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        host = new HostBuilder().UseEnvironment("Production")
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureWebHost(web => web.UseContentRoot(Path.Combine(root, "host")).UseKestrel()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(provider => AiQueryOptions.FromConfiguration(config,
                        provider.GetRequiredService<IWebHostEnvironment>()));
                    services.AddAiTrustedIngress(config);
                    // Register listener creation after the real endpoint-default capture configuration.
                    services.Configure<KestrelServerOptions>(server =>
                    {
                        if (OmitCapture) server.ConfigureEndpointDefaults(_ => { });
                        server.Listen(IPAddress.Loopback, 0, endpoint => endpoint.Protocols = Http2 ? HttpProtocols.Http2 : HttpProtocols.Http1);
                    });
                })
                .Configure(app =>
                {
                    app.Use(async (context, next) =>
                    {
                        TaskCompletionSource done;
                        lock (sync)
                        {
                            if (!accepting) { context.Response.StatusCode = 503; return; }
                            done = new(TaskCreationOptions.RunContinuationsAsynchronously); completions.Add(done.Task);
                        }
                        try { await next(); } finally { done.TrySetResult(); }
                    });
                    if (RewritePeer) app.Use(next => context =>
                    { context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.99"); return next(context); });
                    app.UseAiTrustedIngress(config);
                    app.Run(async context =>
                    {
                        Observations.Enqueue(new(context.Connection.Id, context.Features.Get<IHttp2StreamIdFeature>()?.StreamId,
                            context.Request.Protocol, context.Features.Get<AiConnectionPeer>()?.Address,
                            context.Features.Get<AiClientIdentity>()?.GetAddress()?.ToString(), context.Request.Scheme));
                        if (HttpMethods.IsGet(context.Request.Method)) { context.Response.StatusCode = 200; return; }
                        var admitted = new AdmittedStructuredQueryGenerator(new Generator(this), policy, budget, context, clock);
                        try { await admitted.GenerateAsync("devices", vocabulary, context.RequestAborted); context.Response.StatusCode = 200; }
                        catch (AiAdmissionRejectedException error)
                        { context.Response.StatusCode = error.Reason == AiAdmissionRejectedException.Rejection.RateLimited ? 429 : 503; }
                    });
                })).Build();
        await host.StartAsync();
        CaptureBinding();
        if (StartupFailure is not null) ExceptionDispatchInfo.Capture(StartupFailure).Throw();
        client = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false, MaxConnectionsPerServer = 1 })
        {
            BaseAddress = Address, Timeout = TimeSpan.FromSeconds(8),
            DefaultRequestVersion = Http2 ? HttpVersion.Version20 : HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
    }

    /// <summary>Retains only positively observed effective listener evidence, including after a post-bind failure.</summary>
    private void CaptureBinding()
    {
        var addresses = host!.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        var binding = new Uri(Assert.Single(addresses));
        Assert.Equal("http", binding.Scheme); Assert.True(IPAddress.IsLoopback(IPAddress.Parse(binding.Host))); Assert.True(binding.Port > 0);
        Address = binding;
    }

    /// <summary>Stops accepting owned work, drains actual requests, then disposes resources before deleting only known files.</summary>
    private async Task CleanupAsync()
    {
        ReleaseProviders();
        lock (sync) accepting = false;
        var failures = new List<Exception>();
        void Keep(Exception error) => failures.Add(error);
        try { client?.Dispose(); } catch (Exception error) { Keep(error); }
        if (host is not null)
        {
            if (Address is null)
            {
                try
                {
                    var bindings = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
                    if (bindings is { Count: > 0 }) CaptureBinding(); else NoBoundListenerObserved = true;
                }
                catch (Exception error) { Keep(error); }
            }
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await ObserveAsync(host.StopAsync(stop.Token), TimeSpan.FromSeconds(8)); } catch (Exception error) { Keep(error); }
            // A cancelled HTTP client is not evidence that middleware/provider/counter work has finished.
            Task[] requests; lock (sync) requests = completions.ToArray();
            await ObserveAsync(Task.WhenAll(requests), TimeSpan.FromSeconds(10));
            try { await ObserveAsync(((IAsyncDisposable)host).DisposeAsync().AsTask(), TimeSpan.FromSeconds(8)); }
            catch (Exception error) { Keep(error); }
        }
        if (budget is not null) { try { await budget.DisposeAsync(); } catch (Exception error) { Keep(error); } }
        try { policy?.Dispose(); } catch (Exception error) { Keep(error); }
        if (Address is not null)
        {
            try
            {
                using var tcp = new TcpClient(AddressFamily.InterNetwork);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await tcp.ConnectAsync(IPAddress.Loopback, Address.Port, deadline.Token); }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionRefused) { ListenerClosed = true; }
                Assert.True(ListenerClosed, "A new TCP connection must be refused after cleanup.");
            }
            catch (Exception error) { Keep(error); }
        }
        try
        {
            // Resolve before deletion; never recursively delete a computed directory or discover files to adopt.
            var resolved = Path.GetFullPath(root);
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("HealthMonitor-ai-ingress-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe ingress cleanup path.");
            File.Delete(CounterPath); File.Delete(CounterPath + ".lock");
            if (Directory.Exists(Path.Combine(root, "host"))) Directory.Delete(Path.Combine(root, "host"));
            if (Directory.Exists(root)) Directory.Delete(root);
        }
        catch (Exception error) { Keep(error); }
        if (failures.Count > 0) throw new AggregateException("Ingress cleanup failed.", failures);
    }

    /// <summary>Fail-stops instead of releasing resources beneath unresolved setup, request or shutdown tasks.</summary>
    private static async Task ObserveAsync(Task work, TimeSpan deadline)
    {
        using var timer = new CancellationTokenSource();
        if (await Task.WhenAny(work, Task.Delay(deadline, timer.Token)) != work)
        {
            Console.Error.WriteLine("Owned ingress work exceeded its deadline; stopping without unsafe cleanup.");
            Environment.Exit(1);
        }
        timer.Cancel(); await work;
    }

    /// <summary>Uses a fixed UTC day so reservations do not depend on runner locale or wall-clock midnight.</summary>
    private sealed class Clock : TimeProvider
    {
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    }

    /// <summary>Counts actual admitted attempts and provides an explicit two-request overlap barrier.</summary>
    private sealed class Generator(KestrelAiIngressTestScenario owner) : IStructuredQueryGenerator
    {
        /// <inheritdoc />
        public async Task<StructuredQueryProposal?> GenerateAsync(string prompt, QueryVocabulary vocabulary, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref owner.calls) == 2) owner.overlapping.TrySetResult();
            if (owner.HoldProviders) await owner.release.Task.WaitAsync(cancellationToken);
            return null; // The endpoint verifies admission only; Application proposal semantics are tested separately.
        }
    }
}
