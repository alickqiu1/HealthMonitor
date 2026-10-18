using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using HealthMonitor.Application.Telemetry;
using HealthMonitor.Application.Device;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HealthMonitor.IntegrationTests.Infrastructure;

/// <summary>Owns the real MVC/security pipeline with a fake ingestion port and fabricated listener/connection facts.</summary>
/// <remarks>TestServer does not bind Kestrel listeners. These tests prove gate consumption, not real effective-host, proxy, TLS, or database behavior.</remarks>
internal sealed class SimulationTestHost : IAsyncDisposable
{
    internal const string Route = "/api/v1/simulation/batches";
    internal ReadApiTestHost Http { get; }
    internal FakeIngestion Ingestion { get; }
    internal NetworkFacts Network { get; }

    /// <summary>Captures one started feature host; its independent server-drain barriers retain their original ownership.</summary>
    private SimulationTestHost(ReadApiTestHost http, FakeIngestion ingestion, NetworkFacts network)
    { Http = http; Ingestion = ingestion; Network = network; }

    /// <summary>Creates only explicit nonsecret flags and the single test SPA origin; no runtime settings or database source is loaded.</summary>
    /// <param name="enabled">Explicit endpoint opt-in under test.</param>
    /// <param name="anonymous">Explicit local anonymous opt-in under test.</param>
    /// <param name="environment">Actual host environment supplied to both test builders.</param>
    /// <returns>Caller-owned host with fabricated loopback evidence and fake Application ports.</returns>
    internal static async Task<SimulationTestHost> StartAsync(bool enabled = true, bool anonymous = true, string environment = "Development")
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Simulation:HttpEndpointEnabled"] = enabled.ToString(), ["Simulation:AllowLocalAnonymous"] = anonymous.ToString(),
            ["Cors:AllowedOrigins:0"] = "http://localhost:5173"
        }).Build();
        var port = new FakeIngestion(); var network = new NetworkFacts();
        var host = await ReadApiTestHost.StartAsync(services =>
        {
            // Development validates all descriptors at startup; remove real query registrations rather than weakening validation.
            var reads = new FakeReadPorts();
            services.RemoveAll<IDeviceHealthQueries>(); services.RemoveAll<IDeviceHistoryQueries>(); services.RemoveAll<TimeProvider>();
            services.AddSingleton<IDeviceCatalogQueries>(reads); services.AddSingleton<IDeviceHealthQueries>(reads);
            services.AddSingleton<IDeviceHistoryQueries>(reads); services.AddSingleton<TimeProvider>(reads);
            services.AddSingleton<ITelemetryIngestionService>(port);
            services.AddSingleton<IStartupFilter>(new NetworkFilter(network));
        }, providerSpecificBroadRule: true, simulationConfiguration: config, environmentName: environment);
        var result = new SimulationTestHost(host, port, network);
        result.SetBindings("http://127.0.0.1:5000");
        return result;
    }

    /// <summary>Changes only the fabricated feature inspected by the production gate, including missing evidence.</summary>
    /// <param name="addresses">Effective-address test facts; null removes the feature and an empty array supplies no addresses.</param>
    internal void SetBindings(params string[]? addresses) => ApplyBindings(Http, addresses);

    /// <summary>Shares fabricated network evidence with the real PostgreSQL feature fixture without substituting its ingestion service.</summary>
    internal static void ApplyBindings(ReadApiTestHost host, params string[]? addresses) => host.Services.GetRequiredService<IServer>().Features
        .Set<IServerAddressesFeature>(addresses is null ? null : new BindingFacts(addresses));

    /// <summary>Sends known-length or deliberately streamed UTF-8 JSON and transfers response ownership to the test.</summary>
    /// <param name="json">Raw body, including intentional malformed JSON or boundary padding.</param>
    /// <param name="chunked">True prevents Content-Length computation.</param>
    /// <param name="mediaType">Content type being tested.</param>
    /// <param name="query">Optional query suffix, including its question mark.</param>
    /// <returns>Caller-owned buffered response after the request finishes.</returns>
    internal async Task<HttpResponseMessage> PostAsync(string json, bool chunked = false, string mediaType = "application/json", string? query = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Route + query);
        request.Content = chunked ? new StreamedBody(json) : new StringContent(json, Encoding.UTF8, mediaType);
        if (chunked) { request.Headers.TransferEncodingChunked = true; request.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType); }
        // Spoofed routing/proxy claims must never substitute for actual connection facts.
        request.Headers.Host = "localhost"; request.Headers.Add("X-Forwarded-For", "127.0.0.1");
        return await Http.Client.SendAsync(request);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Http.DisposeAsync();

    /// <summary>Uses the existing Application boundary, with recorded immutable request snapshots and controllable acknowledgment/failure.</summary>
    internal sealed class FakeIngestion : ITelemetryIngestionService
    {
        internal ConcurrentQueue<TelemetryIngestionRequest> Requests { get; } = new();
        internal Func<TelemetryIngestionRequest, CancellationToken, Task<TelemetryIngestionResult>>? Behavior { get; set; }
        internal DateTimeOffset CommittedAt { get; } = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        /// <inheritdoc />
        public Task<TelemetryIngestionResult> IngestAsync(TelemetryIngestionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests.Enqueue(request);
            return Behavior?.Invoke(request, cancellationToken)
                ?? Task.FromResult(new TelemetryIngestionResult(request.BatchId, request.DeviceIds.Count, CommittedAt, false));
        }
    }

    /// <summary>Mutable test-only connection facts; forwarded headers never update these values.</summary>
    internal sealed class NetworkFacts
    {
        internal IPAddress? Local { get; set; } = IPAddress.Loopback;
        internal IPAddress? Remote { get; set; } = IPAddress.Loopback;
    }

    /// <summary>Sets controlled TestServer connection facts before the production security pipeline.</summary>
    internal sealed class NetworkFilter(NetworkFacts facts) : IStartupFilter
    {
        /// <inheritdoc />
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            { context.Connection.LocalIpAddress = facts.Local; context.Connection.RemoteIpAddress = facts.Remote; await continuation(context); });
            next(app);
        };
    }

    /// <summary>Represents controlled effective-address evidence, not an actual bound socket.</summary>
    private sealed class BindingFacts(string[] addresses) : IServerAddressesFeature
    {
        public ICollection<string> Addresses { get; } = addresses.ToList();
        public bool PreferHostingUrls { get; set; }
    }

    /// <summary>Prevents Content-Length from being computed so byte-limit tests exercise the streamed-body path.</summary>
    /// <param name="json">UTF-8 payload including any intentional trailing whitespace.</param>
    private sealed class StreamedBody(string json) : HttpContent
    {
        /// <inheritdoc />
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        /// <inheritdoc />
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(Encoding.UTF8.GetBytes(json)).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(json)));
    }
}
