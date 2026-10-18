using System.Collections.Concurrent;
using System.Text;
using HealthMonitor.Api.AiQueries;
using HealthMonitor.Application.AiQueries;
using HealthMonitor.Application.AiQueries.Ports;
using HealthMonitor.Application.Device;
using HealthMonitor.Application.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HealthMonitor.IntegrationTests.Infrastructure;

/// <summary>Activates only the AI controller in an isolated shared feature host with controlled fake ports.</summary>
/// <remarks>Production composition still receives Enabled=false. Removing its one discovery provider here is
/// a test-only override, not a runtime enablement path or evidence that provider admission is implemented.</remarks>
internal sealed class AiQueryTestHost : IAsyncDisposable
{
    internal const string Route = "/api/v1/ai/device-queries";
    internal ReadApiTestHost Http { get; }
    internal FakeReadPorts Reads { get; }
    internal Generator Generation { get; }
    internal IngestionSpy Ingestion { get; }

    /// <summary>Retains the explicit feature host and its observable non-network doubles.</summary>
    private AiQueryTestHost(ReadApiTestHost http, FakeReadPorts reads, Generator generation, IngestionSpy ingestion)
    { Http = http; Reads = reads; Generation = generation; Ingestion = ingestion; }

    /// <summary>Builds the shared production contract pipeline and optionally removes only the AI discovery gate.</summary>
    /// <param name="activateController">Test-only discovery override; false verifies real default-disabled composition.</param>
    /// <returns>Caller-owned TestServer host, never full Program, secrets, database configuration, or a provider client.</returns>
    internal static async Task<AiQueryTestHost> StartAsync(bool activateController = true)
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
            if (activateController)
            {
                var manager = services.AddControllers().PartManager;
                var gate = manager.FeatureProviders.OfType<AiQueryControllerFeatureProvider>().Single();
                manager.FeatureProviders.Remove(gate);
            }
        }, providerSpecificBroadRule: true, simulationConfiguration: configuration);
        return new(http, reads, generation, ingestion);
    }

    /// <summary>Posts explicit JSON through the client's ten-second bound; server completion is independently drained.</summary>
    internal Task<HttpResponseMessage> PostAsync(string json, CancellationToken token = default) =>
        Http.Client.PostAsync(Route, new StringContent(json, Encoding.UTF8, "application/json"), token);

    /// <summary>Delegates bounded server draining and disposal to the shared host.</summary>
    public ValueTask DisposeAsync() => Http.DisposeAsync();

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
