using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HealthMonitor.Api.Composition;
using HealthMonitor.Application.AiQueries;
using HealthMonitor.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HealthMonitor.IntegrationTests.Api;

/// <summary>Ten fake-provider HTTP cases through shared MVC, strict body validation, CORS, and safe response policies.</summary>
/// <remarks>The test-only discovery override does not enable production AI or prove transport, admission, model accuracy, or hosted behavior.</remarks>
public sealed class AiQueryContractTests
{
    private const string Canary = "private-ai-canary-42";

    [Fact]
    public async Task Disabled_AbsentAndCannotEnable()
    {
        await using var host = await AiQueryTestHost.StartAsync(false);
        using var response = await host.PostAsync(Payload(Canary));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var actions = host.Http.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items;
        Assert.DoesNotContain(actions, action => action.DisplayName?.Contains("AiDeviceQueriesController", StringComparison.Ordinal) == true);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["AiQueries:Enabled"] = "true" }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAiQueryApi(configuration));
        Assert.Empty(host.Generation.Calls); NoStorage(host); SafeLogs(host);
        // An absent endpoint retains the existing generic 404 policy; no matched-AI no-store claim is made.
    }

    [Fact]
    public async Task Ready_CanonicalWireAndZeroStorage()
    {
        await using var host = await AiQueryTestHost.StartAsync();
        using (var response = await host.PostAsync(Payload("  " + Canary + "  ")))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            Assert.Equal(4, root.EnumerateObject().Count());
            Assert.Equal("1", root.GetProperty("contractVersion").GetString());
            Assert.Equal("Ready", root.GetProperty("outcome").GetString());
            Assert.Equal("Review the proposed filters before applying them.", root.GetProperty("message").GetString());
            var query = root.GetProperty("query"); Assert.Equal(6, query.EnumerateObject().Count());
            foreach (var key in new[] { "q", "site", "type", "status" }) Assert.Equal(JsonValueKind.Null, query.GetProperty(key).ValueKind);
            Assert.Equal("displayName", query.GetProperty("sort").GetString()); Assert.Equal("asc", query.GetProperty("direction").GetString());
        }
        host.Generation.Generate = (_, _, _) => Task.FromResult<StructuredQueryProposal?>(new("Ready",
            new(" " + Canary + "%_[' ", " den ", "server", "Offline", "latencyMs", "desc"), "ready"));
        using (var response = await host.PostAsync(Payload("devices")))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var query = document.RootElement.GetProperty("query");
            Assert.Equal(Canary + "%_['", query.GetProperty("q").GetString()); Assert.Equal("DEN", query.GetProperty("site").GetString());
            Assert.Equal("Server", query.GetProperty("type").GetString()); Assert.Equal("Offline", query.GetProperty("status").GetString());
            Assert.Equal("latencyMs", query.GetProperty("sort").GetString()); Assert.Equal("desc", query.GetProperty("direction").GetString());
        }
        Assert.Equal(2, host.Generation.Calls.Count); Assert.Equal(Canary, host.Generation.Calls.First().Prompt);
        Assert.Equal(3, host.Generation.Calls.First().Vocabulary.Sites.Count);
        NoStorage(host); SafeLogs(host);
    }

    [Fact]
    public async Task ClarificationAndUnsupported_FixedWire()
    {
        await using var host = await AiQueryTestHost.StartAsync();
        foreach (var (outcome, reason) in new[] { ("NeedsClarification", "ambiguous"), ("NeedsClarification", "no_interpretation"), ("Unsupported", "unsupported") })
        {
            host.Generation.Generate = (_, _, _) => Task.FromResult<StructuredQueryProposal?>(new(outcome, null, reason));
            using var response = await host.PostAsync(Payload(Canary));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response);
            var text = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain(Canary, text);
            using var document = JsonDocument.Parse(text); var root = document.RootElement;
            Assert.Equal(4, root.EnumerateObject().Count()); Assert.Equal("1", root.GetProperty("contractVersion").GetString());
            Assert.Equal(outcome, root.GetProperty("outcome").GetString()); Assert.Equal(JsonValueKind.Null, root.GetProperty("query").ValueKind);
            Assert.Equal(outcome == "Unsupported" ? "Only supported device filter queries can be proposed."
                : "Please name Healthy, Degraded, Offline, or Unknown and any desired site or device type.", root.GetProperty("message").GetString());
        }
        Assert.Equal(3, host.Generation.Calls.Count); NoStorage(host); SafeLogs(host);
    }

    [Fact]
    public async Task InvalidPromptAndQuery_NoGeneration()
    {
        await using var host = await AiQueryTestHost.StartAsync();
        foreach (var prompt in new[] { "", " \t ", Canary + new string('x', 501) })
        {
            using var response = await host.PostAsync(Payload(prompt));
            await Problem(host, response, HttpStatusCode.BadRequest, "ai_prompt_invalid");
        }
        using (var response = await host.Http.Client.PostAsync(AiQueryTestHost.Route + "?q=" + Canary,
            new StringContent(Payload("devices"), Encoding.UTF8, "application/json")))
            await Problem(host, response, HttpStatusCode.BadRequest, "ai_prompt_invalid");
        Assert.Empty(host.Generation.Calls);
        using (var response = await host.PostAsync(Payload(" " + new string('x', 500) + " ")))
        { Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response); }
        Assert.Single(host.Generation.Calls); NoStorage(host); SafeLogs(host);
    }

    [Fact]
    public async Task StrictBodyShape_NoGeneration()
    {
        await using var host = await AiQueryTestHost.StartAsync();
        foreach (var json in new[] { "null", "[]", "{}", "{", "{\"Prompt\":\"x\"}", "{\"prompt\":null}",
            "{\"prompt\":0}", "{\"prompt\":true}", "{\"prompt\":[]}", "{\"prompt\":{}}", "{\"prompt\":\"x\",\"other\":\"" + Canary + "\"}",
            "{\"prompt\":\"x\",\"prompt\":\"y\"}", "{\"prompt\":\"x\",\"prom\\u0070t\":\"y\"}",
            "{\"prompt\":\"\\uD800\"}", "{\"prompt\":\"x\",}", "/*comment*/{\"prompt\":\"x\"}", "{\"prompt\":\"x\"} null" })
        {
            using var response = await host.PostAsync(json);
            await Problem(host, response, HttpStatusCode.BadRequest, "ai_prompt_invalid");
        }
        using (var request = new HttpRequestMessage(HttpMethod.Post, AiQueryTestHost.Route)
        { Content = new ByteArrayContent([0x7b, 0x22, 0x70, 0x72, 0x6f, 0x6d, 0x70, 0x74, 0x22, 0x3a, 0x22, 0xff, 0x22, 0x7d]) })
        {
            request.Content.Headers.ContentType = new("application/json");
            using var response = await host.Http.Client.SendAsync(request);
            await Problem(host, response, HttpStatusCode.BadRequest, "ai_prompt_invalid");
        }
        using (var response = await host.Http.Client.PostAsync(AiQueryTestHost.Route, new StringContent(Payload("x"))))
            await Problem(host, response, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        foreach (var contentType in new[] { "application/json; charset=utf-16", "application/json; charset=" + Canary,
            "application/json; charset=utf-8; charset=utf-16", "application/json; charset=utf-8; charset=utf-8",
            "application/json; charset=", "application/json; charset=\"utf-8" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, AiQueryTestHost.Route)
                { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Payload(Canary))) };
            Assert.True(request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType));
            using var response = await host.Http.Client.SendAsync(request);
            await Problem(host, response, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        }
        Assert.Empty(host.Generation.Calls);
        foreach (var contentType in new[] { "application/json", "application/json; charset=utf-8",
            "application/json; charset=\"UtF-8\"", "application/vnd.healthmonitor+json; charset=utf-8" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, AiQueryTestHost.Route)
                { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Payload(Canary))) };
            Assert.True(request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType));
            using var response = await host.Http.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response);
        }
        Assert.Equal(4, host.Generation.Calls.Count); NoStorage(host); SafeLogs(host);
    }

    [Fact]
    public async Task BodyByteBounds_KnownStreamedUtf8()
    {
        await using var host = await AiQueryTestHost.StartAsync();
        var valid = Payload("x");
        foreach (bool streamed in new[] { false, true })
        {
            var exact = Encoding.UTF8.GetBytes(valid + new string(' ', 8192 - Encoding.UTF8.GetByteCount(valid)));
            using (var request = Request(exact, streamed))
            using (var response = await host.Http.Client.SendAsync(request))
            { Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response); }
            foreach (var overflow in new[] { exact.Concat(new byte[] { 0x20 }).ToArray(), Encoding.UTF8.GetBytes("{\"prompt\":\"" + new string('界', 2730) + "\"}") })
            {
                Assert.True(overflow.Length > 8192);
                using var request = Request(overflow, streamed);
                using var response = await host.Http.Client.SendAsync(request);
                await Problem(host, response, HttpStatusCode.RequestEntityTooLarge, "request_too_large");
            }
        }
        Assert.Equal(2, host.Generation.Calls.Count); NoStorage(host); SafeLogs(host);
    }

    [Fact]
    public async Task TypedAndUnexpectedFailures_SanitizedAndNoStore()
    {
        await using var host = await AiQueryTestHost.StartAsync();
        host.Generation.Generate = (_, _, _) => Task.FromResult<StructuredQueryProposal?>(new(Canary, null, Canary));
        using (var response = await host.PostAsync(Payload(Canary))) await Problem(host, response, HttpStatusCode.BadGateway, "ai_output_invalid");
        foreach (var error in new Exception[] { new InvalidOperationException(Canary), new ArgumentException(Canary), new OperationCanceledException(Canary) })
        {
            host.Generation.Generate = (_, _, _) => Task.FromException<StructuredQueryProposal?>(error);
            using var response = await host.PostAsync(Payload(Canary));
            await Problem(host, response, HttpStatusCode.InternalServerError, "unexpected_error");
        }
        Assert.Equal(4, host.Generation.Calls.Count); NoStorage(host); SafeLogs(host);
    }

    [Fact]
    public async Task Cancellation_ForwardsAndDrains()
    {
        await using var host = await AiQueryTestHost.StartAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Generation.Generate = async (_, _, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return null; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled.TrySetResult(); throw; }
        };
        using var abort = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = host.PostAsync(Payload(Canary), abort.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Assert.Single(host.Generation.Calls).Token.IsCancellationRequested);
        NoStorage(host); SafeLogs(host);
        // Shared host disposal independently drains the server; client cancellation alone is not completion proof.
    }

    [Fact]
    public async Task Cors_PostOnlyExactOrigins()
    {
        await using var host = await AiQueryTestHost.StartAsync();
        using (var request = Preflight(AiQueryTestHost.Route, "http://localhost:5173", "POST"))
        using (var response = await host.Http.Client.SendAsync(request))
        {
            Assert.Equal("http://localhost:5173", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
            Assert.Contains("POST", response.Headers.GetValues("Access-Control-Allow-Methods"));
            Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        }
        foreach (var origin in new[] { "null", "http://localhost:5173.evil.test", "http://localhost:5174" })
        {
            using var request = Preflight(AiQueryTestHost.Route, origin, "POST");
            using var response = await host.Http.Client.SendAsync(request);
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        }
        var policies = host.Http.Services.GetRequiredService<IOptions<CorsOptions>>().Value;
        Assert.Equal(new[] { "POST" }, policies.GetPolicy(AiQueryApiComposition.CorsPolicyName)!.Methods);
        Assert.Equal(new[] { "GET" }, policies.GetPolicy(ReadCorsComposition.PolicyName)!.Methods);
        using (var request = Preflight("/api/v1/devices", "http://localhost:5173", "POST"))
        using (var response = await host.Http.Client.SendAsync(request))
            Assert.False(response.Headers.TryGetValues("Access-Control-Allow-Methods", out var allowed) && allowed.Contains("POST"));
        // An unmatched method can inherit default read CORS; actual routing must still reject that method.
        Assert.Empty(host.Generation.Calls);
        using (var response = await host.Http.Client.GetAsync(AiQueryTestHost.Route))
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Empty(host.Generation.Calls);
        using (var request = new HttpRequestMessage(HttpMethod.Post, AiQueryTestHost.Route) { Content = new StringContent(Payload("devices"), Encoding.UTF8, "application/json") })
        {
            request.Headers.Add("Origin", "http://localhost:5173");
            using var response = await host.Http.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response);
            Assert.Equal("http://localhost:5173", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        }
        NoStorage(host); SafeLogs(host);
    }

    [Fact]
    public async Task SingleSubmission_GeneratesOnceAndSubsequentGetDoesNotRegenerate()
    {
        await using var host = await AiQueryTestHost.StartAsync();
        using (var response = await host.PostAsync(Payload("devices"))) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(host.Generation.Calls); NoStorage(host);
        // GET intentionally uses fake storage ports here; only translation is required to perform zero storage work.
        host.Reads.Failure = null; host.Reads.AddDevice(1);
        using (var response = await host.Http.Client.GetAsync("/api/v1/devices?page=1&pageSize=25")) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(host.Generation.Calls); Assert.True(host.Reads.CatalogCalls > 0); Assert.Equal(0, host.Ingestion.Calls); SafeLogs(host);
    }

    /// <summary>Builds exactly one string field using the ordinary wire serializer.</summary>
    private static string Payload(string prompt) => JsonSerializer.Serialize(new { prompt });

    /// <summary>Proves no read, clock, or write port was reached during a translation-only scenario.</summary>
    private static void NoStorage(AiQueryTestHost host)
    { Assert.Equal(0, host.Reads.CatalogCalls); Assert.Equal(0, host.Reads.TelemetryCalls); Assert.Equal(0, host.Reads.HistoryCalls); Assert.Equal(0, host.Reads.ClockReads); Assert.Equal(0, host.Ingestion.Calls); }

    /// <summary>Checks every captured log under deliberately broad provider rules for prompt/output leakage.</summary>
    private static void SafeLogs(AiQueryTestHost host) => Assert.All(host.Http.Logs.Entries, entry => Assert.DoesNotContain(Canary, entry));

    /// <summary>Requires the matched AI endpoint's response-start no-store policy.</summary>
    private static void NoStore(HttpResponseMessage response) => Assert.True(response.Headers.CacheControl?.NoStore == true);

    /// <summary>Checks contextual safe problem shape, trace, no-store, and the complete captured diagnostic stream.</summary>
    private static async Task Problem(AiQueryTestHost host, HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode); NoStore(response);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain(Canary, text);
        using var document = JsonDocument.Parse(text); var root = document.RootElement;
        Assert.Equal((int)status, root.GetProperty("status").GetInt32()); Assert.Equal(code, root.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString())); SafeLogs(host);
    }

    /// <summary>Creates known-length or explicitly streamed JSON without changing production request handling.</summary>
    private static HttpRequestMessage Request(byte[] bytes, bool streamed)
    {
        HttpContent content = streamed ? new StreamedJson(bytes) : new ByteArrayContent(bytes);
        content.Headers.ContentType = new("application/json");
        return new(HttpMethod.Post, AiQueryTestHost.Route) { Content = content };
    }

    /// <summary>Creates an ordinary CORS preflight, never an authentication or authorization credential.</summary>
    private static HttpRequestMessage Preflight(string path, string origin, string method)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin); request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", "Content-Type"); return request;
    }

    /// <summary>Declines a declared length so the server must enforce its actual-byte limit while reading.</summary>
    private sealed class StreamedJson(byte[] bytes) : HttpContent
    {
        /// <inheritdoc />
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        /// <inheritdoc />
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
