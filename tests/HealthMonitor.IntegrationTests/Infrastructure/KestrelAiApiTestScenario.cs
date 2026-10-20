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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HealthMonitor.IntegrationTests.Infrastructure;

/// <summary>Owns a real MVC/Kestrel AI host with explicit synthetic configuration and a disposable fake provider.</summary>
/// <remarks>Does not execute Program, load settings/User Secrets, register database workers or call an external provider.
/// Host shutdown and container disposal are separate phases. Actual server completion precedes owned-file cleanup.</remarks>
internal sealed class KestrelAiApiTestScenario
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "HealthMonitor-ai-api-" + Guid.NewGuid().ToString("N"));
    private readonly object sync = new();
    private readonly List<Task> requests = [];
    private readonly List<Task> clients = [];
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IHost? host;
    private ConfigurationRoot? configuration;
    private HttpClient? client;
    private bool accepting = true;
    private bool disposed;
    private bool startAttempted;
    private Task? stopWork;
    private int calls;
    private int providerDisposals;
    internal Dictionary<string, string?> Settings { get; } = new();
    internal bool Enabled { get; init; } = true;
    internal string Profile { get; init; } = "Groq";
    internal string? BudgetContents { get; init; } = "{\"version\":1,\"day\":\"2026-10-20\",\"count\":0}";
    internal bool HoldProvider { get; init; }
    internal Exception? ProviderFailure { get; init; }
    internal bool ExpectStartupFailure { get; init; }
    internal Exception? StartupFailure { get; private set; }
    internal Uri? Address { get; private set; }
    /// <summary>True only when the controlled builder rejected settings before any StartAsync invocation.</summary>
    internal bool RejectedBeforeHostStart => StartupFailure is not null && host is null && !startAttempted;
    /// <summary>Positive failed-start observation of an existing server's empty address feature; unknown remains false.</summary>
    internal bool NoBoundListenerObserved { get; private set; }
    internal int Calls => Volatile.Read(ref calls);
    internal int ProviderDisposals => Volatile.Read(ref providerDisposals);
    internal int SensitiveReads { get; private set; }
    internal string CounterPath => Path.Combine(root, "counter.json");
    internal Task ProviderEntered => entered.Task;
    internal Task HostStopping => stopping.Task;
    internal AiQueryRuntime Runtime => host!.Services.GetRequiredService<AiQueryRuntime>();

    /// <summary>Runs one scenario with bounded setup, assertions and independent resource cleanup.</summary>
    /// <param name="body">Assertions after startup, or after an explicitly expected startup rejection.</param>
    /// <returns>Completion after clients, server requests, host and owned resources are terminal.</returns>
    internal async Task RunAsync(Func<KestrelAiApiTestScenario, Task> body)
    {
        Exception? failure = null;
        try
        {
            try { await ObserveAsync(StartAsync(), TimeSpan.FromSeconds(15)); }
            catch (InvalidOperationException error) when (ExpectStartupFailure)
            {
                StartupFailure = error;
                if (host is not null)
                {
                    try
                    {
                        var bindings = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
                            ?? throw new InvalidOperationException("Failed-start bindings could not be observed.");
                        NoBoundListenerObserved = bindings.Count == 0;
                        if (bindings.Count > 0) Address = new Uri(bindings.First());
                    }
                    catch (Exception observation) { throw new AggregateException("Startup rejection and binding observation failed.", error, observation); }
                }
            }
            if (ExpectStartupFailure) Assert.NotNull(StartupFailure);
            await ObserveAsync(body(this), TimeSpan.FromSeconds(25));
        }
        catch (Exception error) { failure = error; }
        try { await CleanupAsync(); }
        catch (Exception cleanup)
        {
            if (failure is not null) throw new AggregateException("AI host assertions and cleanup failed.", failure, cleanup);
            throw;
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Sends through actual MVC; retains the client task independently of its consumer's assertions.</summary>
    internal Task<HttpResponseMessage> SendAsync(string body = "{\"prompt\":\"devices\"}", string? forwarded = null, string media = "application/json")
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "api/v1/ai/device-queries")
        { Content = new StringContent(body, System.Text.Encoding.UTF8, media) };
        if (forwarded is not null)
        {
            message.Headers.Add("X-Forwarded-For", forwarded);
            message.Headers.Add("X-Forwarded-Proto", "https");
        }
        async Task<HttpResponseMessage> SendCoreAsync()
        { using (message) return await client!.SendAsync(message); }
        var work = SendCoreAsync(); lock (sync) clients.Add(work); return work;
    }

    /// <summary>Reads only the owned counter for independently observed durable reservations.</summary>
    internal int Count()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(CounterPath));
        return document.RootElement.GetProperty("count").GetInt32();
    }

    /// <summary>Begins actual host shutdown; this does not imply asynchronous DI disposal has occurred.</summary>
    internal Task StopAsync() => stopWork ??= host!.StopAsync(CancellationToken.None);

    /// <summary>Releases a fake that deliberately ignores caller cancellation until explicitly permitted to finish.</summary>
    internal void ReleaseProvider() => release.TrySetResult();

    /// <summary>Disposes the actual container only after owned server work has drained.</summary>
    internal async Task DisposeHostAsync()
    {
        if (disposed || host is null) return;
        Task[] work; lock (sync) work = requests.ToArray();
        await ObserveAsync(Task.WhenAll(work), TimeSpan.FromSeconds(10));
        await ((IAsyncDisposable)host).DisposeAsync(); disposed = true;
    }

    /// <summary>Uses no default host configuration providers and registers the same optional composition as Program.</summary>
    private async Task StartAsync()
    {
        Directory.CreateDirectory(Path.Combine(root, "host"));
        if (BudgetContents is not null) File.WriteAllText(CounterPath, BudgetContents);
        var values = new Dictionary<string, string?>
        {
            ["AiQueries:Enabled"] = Enabled ? "true" : "false",
            ["AiQueries:Provider"] = Profile,
            ["AiQueries:ApiKey"] = "synthetic-key",
            ["AiQueries:BudgetCounterPath"] = CounterPath
        };
        foreach (var pair in Settings) values[pair.Key] = pair.Value;
        configuration = new ConfigurationRoot([new ExplicitSettings(values, this)]);
        host = new HostBuilder().ConfigureLogging(log => log.ClearProviders())
            .ConfigureWebHost(web => web.UseKestrel()
                .UseContentRoot(Path.Combine(root, "host")).UseEnvironment("Testing")
                .ConfigureServices((context, services) =>
                {
                    services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(15));
                    services.AddSingleton<TimeProvider>(new Clock());
                    services.AddReadApi(); services.AddReadCors(configuration);
                    services.AddAiQueryApi(configuration, context.HostingEnvironment, _ => new Generator(this));
                    // Listen consumes endpoint defaults immediately; register it after the real capture configuration.
                    services.Configure<KestrelServerOptions>(server => server.Listen(IPAddress.Loopback, 0));
                })
                .Configure(app =>
                {
                    // Track completion outside exception handling: a response or cancelled client is not server-drain proof.
                    app.Use(next => async context =>
                    {
                        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        lock (sync)
                        {
                            if (!accepting) { context.Response.StatusCode = 503; return; }
                            requests.Add(done.Task);
                        }
                        try { await next(context); } finally { done.TrySetResult(); }
                    });
                    app.UseReadApiErrors(); app.UseAiTrustedIngress(configuration); app.UseRouting();
                    app.UseAiQueryNoStore(); app.UseCors(ReadCorsComposition.PolicyName);
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                })).Build();
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() => stopping.TrySetResult());
        startAttempted = true;
        await host.StartAsync();
        var addresses = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        Address = new Uri(Assert.Single(addresses));
        Assert.True(IPAddress.IsLoopback(IPAddress.Parse(Address.Host))); Assert.True(Address.Port > 0);
        client = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false })
        { BaseAddress = Address, Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>Preserves uncertain work by fail-stopping; deletes only known files after actual drain and DI cleanup.</summary>
    private async Task CleanupAsync()
    {
        ReleaseProvider(); lock (sync) accepting = false;
        var failures = new List<Exception>();
        if (host is not null && !disposed)
        {
            try { await ObserveAsync(StopAsync(), TimeSpan.FromSeconds(18)); } catch (Exception error) { failures.Add(error); }
        }
        Task[] ownedClients; Task[] ownedRequests;
        lock (sync) { ownedClients = clients.ToArray(); ownedRequests = requests.ToArray(); }
        // Observe failures as well as termination, then keep them alongside any original assertion failure.
        try { await ObserveAsync(Task.WhenAll(ownedClients), TimeSpan.FromSeconds(10)); } catch (Exception error) { failures.Add(error); }
        await ObserveAsync(Task.WhenAll(ownedRequests), TimeSpan.FromSeconds(10));
        // Responses belong to this scenario even if an assertion failed before the caller retrieved them.
        foreach (var completed in ownedClients.OfType<Task<HttpResponseMessage>>())
            if (completed.IsCompletedSuccessfully)
                try { completed.Result.Dispose(); } catch (Exception error) { failures.Add(error); }
        try { await ObserveAsync(DisposeHostAsync(), TimeSpan.FromSeconds(10)); } catch (Exception error) { failures.Add(error); }
        try { client?.Dispose(); } catch (Exception error) { failures.Add(error); }
        try { configuration?.Dispose(); } catch (Exception error) { failures.Add(error); }
        if (Address is not null)
        {
            using var tcp = new TcpClient(AddressFamily.InterNetwork);
            try { await tcp.ConnectAsync(IPAddress.Loopback, Address.Port).WaitAsync(TimeSpan.FromSeconds(3)); failures.Add(new InvalidOperationException("Listener remained open.")); }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionRefused) { }
        }
        // A failed asynchronous container cleanup must never be followed by deletion beneath its resources.
        if (host is null || disposed)
        {
            var resolved = Path.GetFullPath(root);
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("HealthMonitor-ai-api-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe AI test cleanup path.");
            File.Delete(CounterPath); File.Delete(CounterPath + ".lock");
            if (Directory.Exists(Path.Combine(root, "host"))) Directory.Delete(Path.Combine(root, "host"));
            if (Directory.Exists(root)) Directory.Delete(root);
        }
        if (failures.Count > 0) throw new AggregateException("AI host cleanup failed.", failures);
    }

    /// <summary>Does not mistake cancellation for task termination; an unresponsive owned task stops this test process.</summary>
    private static async Task ObserveAsync(Task task, TimeSpan timeout)
    {
        using var timer = new CancellationTokenSource();
        if (await Task.WhenAny(task, Task.Delay(timeout, timer.Token)) != task)
        { Console.Error.WriteLine("Owned AI host work exceeded its deadline; stopping without unsafe cleanup."); Environment.Exit(1); }
        timer.Cancel(); await task;
    }

    /// <summary>Guards disabled configuration against any active-value lookup; keys remain enumerable.</summary>
    private sealed class ExplicitSettings(Dictionary<string, string?> values, KestrelAiApiTestScenario owner) : ConfigurationProvider
    {
        internal void Initialize() => Data = values;
        /// <inheritdoc />
        public override void Load() => Initialize();
        /// <inheritdoc />
        public override bool TryGet(string key, out string? value)
        {
            if (!owner.Enabled && (key.StartsWith("AiQueries:", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("AiQueries:Enabled", StringComparison.OrdinalIgnoreCase) || key.StartsWith("TrustedIngress:", StringComparison.OrdinalIgnoreCase)))
            { owner.SensitiveReads++; throw new InvalidOperationException("Disabled active-setting lookup."); }
            return base.TryGet(key, out value);
        }
    }

    /// <summary>Fixes the UTC day, avoiding locale and midnight-dependent budget assertions.</summary>
    private sealed class Clock : TimeProvider
    {
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    }

    /// <summary>Exposes controlled completion and disposal without network or provider SDK access.</summary>
    private sealed class Generator(KestrelAiApiTestScenario owner) : IStructuredQueryGenerator, IDisposable
    {
        /// <inheritdoc />
        public async Task<StructuredQueryProposal?> GenerateAsync(string prompt, QueryVocabulary vocabulary, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref owner.calls); owner.entered.TrySetResult();
            if (owner.HoldProvider) await owner.release.Task; // Deliberately uncooperative, to prove resource retention until actual termination.
            if (owner.ProviderFailure is not null) ExceptionDispatchInfo.Capture(owner.ProviderFailure).Throw();
            return new("Ready", new(null, " den ", "server", null, "displayName", "asc"), "ready");
        }
        /// <inheritdoc />
        public void Dispose() => Interlocked.Increment(ref owner.providerDisposals);
    }
}
