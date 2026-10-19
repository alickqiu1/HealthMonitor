using System.Collections.Concurrent;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using HealthMonitor.Api.AiQueries;
using HealthMonitor.Application.AiQueries;
using HealthMonitor.Application.AiQueries.Ports;
using HealthMonitor.Application.Device;
using HealthMonitor.Application.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HealthMonitor.IntegrationTests.Infrastructure;

/// <summary>Activates only the AI controller in an isolated shared feature host with controlled fake ports.</summary>
/// <remarks>Production still receives Enabled=false. Test-only controller discovery and admission composition do not
/// enable production AI or prove trusted proxies, effective Kestrel bindings, real provider behavior or hosted operations.</remarks>
internal sealed class AiQueryTestHost : IAsyncDisposable
{
    internal const string Route = "/api/v1/ai/device-queries";
    internal ReadApiTestHost Http { get; }
    internal FakeReadPorts Reads { get; }
    internal Generator Generation { get; }
    internal IngestionSpy Ingestion { get; }
    private readonly AiDailyBudgetStore? budget;
    private readonly AiAdmissionPolicy? policy;

    /// <summary>Retains the feature host, its observable doubles and any explicitly owned admission resources.</summary>
    private AiQueryTestHost(ReadApiTestHost http, FakeReadPorts reads, Generator generation, IngestionSpy ingestion,
        AiAdmissionPolicy? policy, AiDailyBudgetStore? budget)
    { Http = http; Reads = reads; Generation = generation; Ingestion = ingestion; this.policy = policy; this.budget = budget; }

    /// <summary>Builds the original fake-generator contract host with no admission override.</summary>
    /// <param name="activateController">Test-only discovery override; false verifies real default-disabled composition.</param>
    /// <returns>Caller-owned TestServer host without application settings, secrets or a provider connection.</returns>
    internal static Task<AiQueryTestHost> StartAsync(bool activateController = true) => StartCoreAsync(activateController, null, null, null, null);

    /// <summary>Composes the real local admission controls around the fake provider, using only an explicit temporary counter.</summary>
    /// <param name="counterPath">Test-owned pre-provisioned counter path, or intentionally missing state for rejection tests.</param>
    /// <param name="clock">Shared controlled UTC clock for both counter and decorator.</param>
    /// <param name="transportAddress">Controlled connection fact, never derived from a request header.</param>
    /// <returns>Caller-owned host; startup failures release every created resource without hiding the primary failure.</returns>
    internal static async Task<AiQueryTestHost> StartWithAdmissionAsync(string counterPath, TimeProvider clock, Func<IPAddress?> transportAddress)
    {
        ArgumentNullException.ThrowIfNull(clock); ArgumentNullException.ThrowIfNull(transportAddress);
        var policy = new AiAdmissionPolicy();
        AiDailyBudgetStore? budget = null;
        try
        {
            budget = new(counterPath, clock);
            return await StartCoreAsync(true, policy, budget, clock, transportAddress);
        }
        catch (Exception original)
        {
            await DisposeAdmissionAsync(budget, policy, original);
            throw;
        }
    }

    /// <summary>Shares only explicit feature setup; admission remains a separate test-only entry point.</summary>
    private static async Task<AiQueryTestHost> StartCoreAsync(bool activateController, AiAdmissionPolicy? policy,
        AiDailyBudgetStore? budget, TimeProvider? clock, Func<IPAddress?>? transportAddress)
    {
        var reads = new FakeReadPorts { Failure = new InvalidOperationException("Unexpected storage access in AI translation.") };
        var generation = new Generator();
        var ingestion = new IngestionSpy();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AiQueries:Enabled"] = "false", ["Simulation:HttpEndpointEnabled"] = "false",
            ["Cors:AllowedOrigins:0"] = "http://localhost:5173"
        }).Build();
        var http = await ReadApiTestHost.StartAsync(services =>
        {
            services.RemoveAll<IDeviceCatalogQueries>(); services.RemoveAll<IDeviceHealthQueries>();
            services.RemoveAll<IDeviceHistoryQueries>(); services.RemoveAll<TimeProvider>();
            services.AddSingleton<IDeviceCatalogQueries>(reads); services.AddSingleton<IDeviceHealthQueries>(reads);
            services.AddSingleton<IDeviceHistoryQueries>(reads); services.AddSingleton<TimeProvider>(reads);
            services.AddSingleton<IStructuredQueryGenerator>(generation);
            services.AddSingleton<ITelemetryIngestionService>(ingestion);
            if (budget is not null)
            {
                services.AddHttpContextAccessor();
                services.RemoveAll<IStructuredQueryGenerator>();
                // Resolve the current context per request and retain the explicit fake inner, never this interface recursively.
                services.AddScoped<IStructuredQueryGenerator>(provider => new AdmittedStructuredQueryGenerator(generation, policy!, budget,
                    provider.GetRequiredService<IHttpContextAccessor>().HttpContext
                        ?? throw new InvalidOperationException("Admission requires an active test request."), clock!));
            }
            if (activateController)
            {
                var manager = services.AddControllers().PartManager;
                var gate = manager.FeatureProviders.OfType<AiQueryControllerFeatureProvider>().Single();
                manager.FeatureProviders.Remove(gate);
            }
        }, providerSpecificBroadRule: true, simulationConfiguration: configuration, transportAddress: transportAddress);
        return new(http, reads, generation, ingestion, policy, budget);
    }

    /// <summary>Posts explicit JSON through the client's ten-second bound; server completion is independently drained.</summary>
    internal Task<HttpResponseMessage> PostAsync(string json, CancellationToken token = default) =>
        Http.Client.PostAsync(Route, new StringContent(json, Encoding.UTF8, "application/json"), token);

    /// <summary>Drains the actual server before disposing counter ownership or limiter permits; preserves all cleanup failures.</summary>
    /// <remarks>The shared host fail-stops on unresolved work rather than throwing permission to clean up live writers.</remarks>
    public async ValueTask DisposeAsync()
    {
        Exception? failure = null;
        try { await Http.DisposeAsync(); }
        catch (Exception error)
        {
            if (!Http.RequestsDrained) throw; // Unknown completion must retain the counter lock and limiter ownership.
            failure = error;
        }
        await DisposeAdmissionAsync(budget, policy, failure);
    }

    /// <summary>Attempts every admission cleanup while retaining original and cleanup failures for the test report.</summary>
    private static async Task DisposeAdmissionAsync(AiDailyBudgetStore? budget, AiAdmissionPolicy? policy, Exception? original)
    {
        var failures = new List<Exception>();
        if (original is not null) failures.Add(original);
        try { if (budget is not null) await budget.DisposeAsync(); }
        catch (Exception error) { failures.Add(error); }
        try { policy?.Dispose(); }
        catch (Exception error) { failures.Add(error); }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Test host operation and cleanup failed.", failures);
    }

    /// <summary>Records every generation call and lets tests control decisions, failures, and cooperative cancellation.</summary>
    internal sealed class Generator : IStructuredQueryGenerator
    {
        internal ConcurrentQueue<(string Prompt, QueryVocabulary Vocabulary, CancellationToken Token)> Calls { get; } = new();
        internal Func<string, QueryVocabulary, CancellationToken, Task<StructuredQueryProposal?>> Generate { get; set; }
            = (_, _, _) => Task.FromResult<StructuredQueryProposal?>(new("Ready",
                new(null, null, null, null, "displayName", "asc"), "ready"));

        /// <summary>Captures only test-owned facts and invokes the controlled operation without transport or storage.</summary>
        public Task<StructuredQueryProposal?> GenerateAsync(string prompt, QueryVocabulary vocabulary, CancellationToken cancellationToken)
        {
            Calls.Enqueue((prompt, vocabulary, cancellationToken));
            return Generate(prompt, vocabulary, cancellationToken);
        }
    }

    /// <summary>Fails immediately if the translation path attempts to enter the write use case.</summary>
    internal sealed class IngestionSpy : ITelemetryIngestionService
    {
        internal int Calls;
        /// <inheritdoc />
        public Task<TelemetryIngestionResult> IngestAsync(TelemetryIngestionRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("Unexpected ingestion access in AI translation.");
        }
    }
}
