using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HealthMonitor.Api.AiQueries;
using HealthMonitor.Api.Composition;
using HealthMonitor.Application.AiQueries;
using HealthMonitor.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HealthMonitor.IntegrationTests.Api;

/// <summary>Nineteen fake-provider HTTP cases through shared MVC, including real local admission, strict validation and safe responses.</summary>
/// <remarks>Local admission uses a controlled TestServer connection fact. This does not enable production AI or prove trusted proxies, real transport, model accuracy or hosted behavior.</remarks>
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

    /// <summary>Distinguishes owned local/provider failures from invalid output and unexpected faults without exposing request or provider content.</summary>
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
        Assert.Equal(4, host.Generation.Calls.Count);
        foreach (bool synchronous in new[] { false, true })
        foreach (var (error, status, code, title) in new (Exception, HttpStatusCode, string, string)[]
        {
            (new AiProviderUnavailableException(), HttpStatusCode.ServiceUnavailable, "ai_provider_unavailable", "The AI provider is unavailable."),
            (new AiProviderTimeoutException(), HttpStatusCode.GatewayTimeout, "ai_provider_timeout", "The AI provider request timed out.")
        })
        {
            host.Generation.Generate = (_, _, _) => synchronous ? throw error : Task.FromException<StructuredQueryProposal?>(error);
            int before = host.Generation.Calls.Count;
            using var response = await host.PostAsync(Payload(Canary));
            await Problem(host, response, status, code);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(title, document.RootElement.GetProperty("title").GetString());
            Assert.Equal(before + 1, host.Generation.Calls.Count);
            // Provider failures do not acquire local rate-limit semantics or forward provider headers.
            Assert.False(response.Headers.Contains("Retry-After"));
        }
        Assert.Equal(8, host.Generation.Calls.Count);
        foreach (bool synchronous in new[] { false, true })
        foreach (var (error, status, code, title, retryAfter) in new (AiAdmissionRejectedException, HttpStatusCode, string, string, string?)[]
        {
            (new(AiAdmissionRejectedException.Rejection.RateLimited, 60), HttpStatusCode.TooManyRequests,
                "ai_rate_limited", "The AI translation request limit was reached.", "60"),
            (new(AiAdmissionRejectedException.Rejection.BudgetExhausted, 43200), HttpStatusCode.TooManyRequests,
                "ai_budget_exhausted", "The daily AI translation budget is exhausted.", "43200"),
            (new(AiAdmissionRejectedException.Rejection.AdmissionUnavailable), HttpStatusCode.ServiceUnavailable,
                "ai_admission_unavailable", "AI admission is unavailable.", null),
            (new(AiAdmissionRejectedException.Rejection.BudgetUnavailable), HttpStatusCode.ServiceUnavailable,
                "ai_budget_unavailable", "The AI usage budget is unavailable.", null)
        })
        {
            // These controlled exceptions prove mapping only; actual admission/counter HTTP integration is a later gate.
            host.Generation.Generate = (_, _, _) => synchronous ? throw error : Task.FromException<StructuredQueryProposal?>(error);
            int before = host.Generation.Calls.Count;
            using var response = await host.PostAsync(Payload(Canary));
            await Problem(host, response, status, code);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(title, document.RootElement.GetProperty("title").GetString());
            if (retryAfter is null) Assert.False(response.Headers.Contains("Retry-After"));
            else Assert.Equal(retryAfter, Assert.Single(response.Headers.GetValues("Retry-After")));
            Assert.Equal(before + 1, host.Generation.Calls.Count);
        }
        Assert.Equal(16, host.Generation.Calls.Count); NoStorage(host); SafeLogs(host);
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

    [Fact]
    public async Task Admission_InvalidRequests_DoNotConsumeBudget() => await AdmissionScenario.RunAsync(async scenario =>
    {
        var host = scenario.Host;
        foreach (string json in new[] { "{", "{}", Payload(""), Payload(" \t "), Payload(new string('x', 501)) })
        {
            using var response = await host.PostAsync(json);
            await Problem(host, response, HttpStatusCode.BadRequest, "ai_prompt_invalid");
        }
        using (var response = await host.Http.Client.PostAsync(AiQueryTestHost.Route + "?q=" + Canary,
            new StringContent(Payload(Canary), Encoding.UTF8, "application/json")))
            await Problem(host, response, HttpStatusCode.BadRequest, "ai_prompt_invalid");
        using (var response = await host.PostAsync(Payload("x") + new string(' ', 8192)))
            await Problem(host, response, HttpStatusCode.RequestEntityTooLarge, "request_too_large");
        Assert.Equal(0, scenario.Count()); Assert.Empty(host.Generation.Calls); Assert.False(File.Exists(scenario.Counter + ".lock"));
        for (int index = 0; index < 5; index++)
        { using var response = await host.PostAsync(Payload(Canary)); Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response); }
        Assert.Equal(5, scenario.Count()); Assert.Equal(5, host.Generation.Calls.Count); AdmissionPrivacy(scenario); NoStorage(host);
    });

    [Fact]
    public async Task Admission_ClientLimit_IgnoresSpoofedForwardingHeaders() => await AdmissionScenario.RunAsync(async scenario =>
    {
        foreach (IPAddress? invalid in new IPAddress?[] { null, IPAddress.Any })
        {
            scenario.Address = invalid;
            using var response = await scenario.PostAsync("198.51.100.42");
            await Problem(scenario.Host, response, HttpStatusCode.ServiceUnavailable, "ai_admission_unavailable");
            Assert.Equal(0, scenario.Count()); Assert.Empty(scenario.Host.Generation.Calls);
        }
        scenario.Address = IPAddress.Parse("192.0.2.1");
        for (int index = 1; index <= 5; index++)
        { using var response = await scenario.PostAsync("198.51.100." + index); Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response); }
        using (var response = await scenario.PostAsync("198.51.100.99"))
        { await Problem(scenario.Host, response, HttpStatusCode.TooManyRequests, "ai_rate_limited"); PositiveRetry(response); }
        Assert.Equal(5, scenario.Count()); Assert.Equal(5, scenario.Host.Generation.Calls.Count); AdmissionPrivacy(scenario); NoStorage(scenario.Host);
    });

    [Fact]
    public async Task Admission_GlobalLimit_UsesTrustedConnectionIdentity() => await AdmissionScenario.RunAsync(async scenario =>
    {
        // Each response completes before changing the test transport fact; no identity is obtained from a header.
        for (int index = 1; index <= 10; index++)
        {
            scenario.Address = IPAddress.Parse("192.0.2." + index);
            using var response = await scenario.Host.PostAsync(Payload(Canary)); Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response);
        }
        scenario.Address = IPAddress.Parse("192.0.2.11");
        using (var response = await scenario.Host.PostAsync(Payload(Canary)))
        { await Problem(scenario.Host, response, HttpStatusCode.TooManyRequests, "ai_rate_limited"); PositiveRetry(response); }
        Assert.Equal(10, scenario.Count()); Assert.Equal(10, scenario.Host.Generation.Calls.Count); AdmissionPrivacy(scenario); NoStorage(scenario.Host);
    });

    [Fact]
    public async Task Admission_Concurrency_DoesNotQueueOrReleaseOnClientAbort() => await AdmissionScenario.RunAsync(async scenario =>
    {
        var firstGate = new TaskCompletionSource<StructuredQueryProposal?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondGate = new TaskCompletionSource<StructuredQueryProposal?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entries = 0;
        scenario.Host.Generation.Generate = async (_, _, token) =>
        {
            int number = Interlocked.Increment(ref entries);
            if (number == 1)
            {
                using var registration = token.Register(() => providerCancelled.TrySetResult());
                firstEntered.TrySetResult();
                return await firstGate.Task; // Deliberately ignores cancellation until this controlled provider actually ends.
            }
            if (number == 2) { secondEntered.TrySetResult(); return await secondGate.Task; }
            return AdmissionReady();
        };
        using var abort = new CancellationTokenSource();
        var first = scenario.Host.PostAsync(Payload(Canary), abort.Token);
        Task<HttpResponseMessage>? second = null;
        await RunAndCleanupAsync(async () =>
        {
            // The first provider entry fixes server arrival zero before another request can enter.
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            scenario.Address = IPAddress.Parse("192.0.2.2"); second = scenario.Host.PostAsync(Payload(Canary));
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            scenario.Address = IPAddress.Parse("192.0.2.3");
            using (var third = await scenario.Host.PostAsync(Payload(Canary)))
                await Problem(scenario.Host, third, HttpStatusCode.TooManyRequests, "ai_rate_limited");
            abort.Cancel();
            // TestServer may keep the client task pending until server termination; observe the actual downstream abort instead.
            await providerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            scenario.Address = IPAddress.Parse("192.0.2.4");
            using (var fourth = await scenario.Host.PostAsync(Payload(Canary)))
                await Problem(scenario.Host, fourth, HttpStatusCode.TooManyRequests, "ai_rate_limited");
            Assert.Equal(2, scenario.Count()); Assert.Equal(2, entries); Assert.False(second.IsCompleted);
            firstGate.TrySetResult(AdmissionReady());
            await scenario.Host.Http.WaitForRequestCompletionAsync(0);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
            scenario.Address = IPAddress.Parse("192.0.2.5");
            using (var fifth = await scenario.Host.PostAsync(Payload(Canary)))
            { Assert.Equal(HttpStatusCode.OK, fifth.StatusCode); NoStore(fifth); }
            Assert.Equal(3, scenario.Count()); Assert.Equal(3, entries); Assert.False(second.IsCompleted);
        }, async () =>
        {
            firstGate.TrySetResult(AdmissionReady()); secondGate.TrySetResult(AdmissionReady());
            await RunAndCleanupAsync(async () =>
            {
                try { using var response = await first.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (OperationCanceledException) when (abort.IsCancellationRequested) { }
            }, async () => { if (second is not null) { using var response = await second.WaitAsync(TimeSpan.FromSeconds(10)); } });
        });
        AdmissionPrivacy(scenario); NoStorage(scenario.Host);
    });

    [Fact]
    public async Task Admission_DailyExhaustion_HasUtcRetryAndNoProviderCall()
    {
        await AdmissionScenario.RunAsync(async scenario =>
        {
            using (var response = await scenario.Host.PostAsync(Payload(Canary)))
            { await Problem(scenario.Host, response, HttpStatusCode.TooManyRequests, "ai_budget_exhausted"); Assert.Equal("43200", Assert.Single(response.Headers.GetValues("Retry-After"))); }
            Assert.Equal(100, scenario.Count()); Assert.Empty(scenario.Host.Generation.Calls);
            scenario.Clock.Now = scenario.Clock.Now.AddDays(1);
            using (var response = await scenario.Host.PostAsync(Payload(Canary)))
            { Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response); Assert.False(response.Headers.Contains("Retry-After")); }
            Assert.Equal(1, scenario.Count()); Assert.Equal("2026-10-19", scenario.Day()); AdmissionPrivacy(scenario); NoStorage(scenario.Host);
        }, count: 100);
        await AdmissionScenario.RunAsync(async scenario =>
        {
            // Overlap HTTP lifetimes after the first reservation commits; competing counter reservations are covered by store unit tests.
            var gate = new TaskCompletionSource<StructuredQueryProposal?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            scenario.Host.Generation.Generate = (_, _, _) => { entered.TrySetResult(); return gate.Task; };
            var first = scenario.Host.PostAsync(Payload(Canary));
            await RunAndCleanupAsync(async () =>
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                scenario.Address = IPAddress.Parse("192.0.2.2");
                using (var second = await scenario.Host.PostAsync(Payload(Canary)))
                    await Problem(scenario.Host, second, HttpStatusCode.TooManyRequests, "ai_budget_exhausted");
                Assert.Equal(100, scenario.Count()); Assert.Single(scenario.Host.Generation.Calls); Assert.False(first.IsCompleted);
            }, async () => { gate.TrySetResult(AdmissionReady()); using var response = await first.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(HttpStatusCode.OK, response.StatusCode); });
            AdmissionPrivacy(scenario); NoStorage(scenario.Host);
            await scenario.RestartAsync();
            using (var response = await scenario.Host.PostAsync(Payload(Canary)))
                await Problem(scenario.Host, response, HttpStatusCode.TooManyRequests, "ai_budget_exhausted");
            Assert.Equal(100, scenario.Count()); Assert.Empty(scenario.Host.Generation.Calls); AdmissionPrivacy(scenario); NoStorage(scenario.Host);
        }, count: 99);
    }

    [Fact]
    public async Task Admission_MissingOrCorruptCounter_FailsClosed()
    {
        foreach (bool corrupt in new[] { false, true })
            await AdmissionScenario.RunAsync(async scenario =>
            {
                if (corrupt) File.WriteAllText(scenario.Counter, "{");
                using (var response = await scenario.Host.PostAsync(Payload(Canary)))
                { await Problem(scenario.Host, response, HttpStatusCode.ServiceUnavailable, "ai_budget_unavailable"); Assert.False(response.Headers.Contains("Retry-After")); }
                Assert.Empty(scenario.Host.Generation.Calls);
                if (corrupt) Assert.Equal("{", File.ReadAllText(scenario.Counter)); else Assert.False(File.Exists(scenario.Counter));
                AdmissionPrivacy(scenario); NoStorage(scenario.Host);
            }, seed: false);
    }

    [Fact]
    public async Task Admission_ProviderFailuresRemainChargedWithoutRetry() => await AdmissionScenario.RunAsync(async scenario =>
    {
        int expected = 0;
        foreach (var (kind, status, code) in new[]
        {
            ("unavailable", HttpStatusCode.ServiceUnavailable, "ai_provider_unavailable"),
            ("timeout", HttpStatusCode.GatewayTimeout, "ai_provider_timeout"),
            ("output", HttpStatusCode.BadGateway, "ai_output_invalid"),
            ("unexpected", HttpStatusCode.InternalServerError, "unexpected_error")
        })
        {
            scenario.Host.Generation.Generate = (_, _, _) => kind switch
            {
                "unavailable" => Task.FromException<StructuredQueryProposal?>(new AiProviderUnavailableException()),
                "timeout" => Task.FromException<StructuredQueryProposal?>(new AiProviderTimeoutException()),
                "output" => Task.FromResult<StructuredQueryProposal?>(new("Ready", new(null, null, null, Canary, "displayName", "asc"), "ready")),
                _ => Task.FromException<StructuredQueryProposal?>(new InvalidOperationException(Canary))
            };
            using var response = await scenario.Host.PostAsync(Payload(Canary)); await Problem(scenario.Host, response, status, code);
            Assert.False(response.Headers.Contains("Retry-After"));
            expected++; Assert.Equal(expected, scenario.Count()); Assert.Equal(expected, scenario.Host.Generation.Calls.Count);
        }
        AdmissionPrivacy(scenario); NoStorage(scenario.Host);
    });

    [Fact]
    public async Task Admission_RestartPreservesCounter() => await AdmissionScenario.RunAsync(async scenario =>
    {
        using (var response = await scenario.Host.PostAsync(Payload(Canary))) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(71, scenario.Count()); AdmissionPrivacy(scenario); NoStorage(scenario.Host);
        await scenario.RestartAsync();
        using (var response = await scenario.Host.PostAsync(Payload(Canary))) { Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response); }
        Assert.Equal(72, scenario.Count()); Assert.Single(scenario.Host.Generation.Calls); AdmissionPrivacy(scenario); NoStorage(scenario.Host);
    }, count: 70);

    [Fact]
    public async Task Admission_ReadyThenGet_DoesNotRegenerate() => await AdmissionScenario.RunAsync(async scenario =>
    {
        using (var response = await scenario.Host.PostAsync(Payload(Canary))) { Assert.Equal(HttpStatusCode.OK, response.StatusCode); NoStore(response); }
        Assert.Equal(1, scenario.Count()); Assert.Single(scenario.Host.Generation.Calls); NoStorage(scenario.Host);
        scenario.Host.Reads.Failure = null; scenario.Host.Reads.AddDevice(1);
        using (var response = await scenario.Host.Http.Client.GetAsync("/api/v1/devices?page=1&pageSize=25")) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(scenario.Host.Generation.Calls); Assert.Equal(1, scenario.Count()); Assert.True(scenario.Host.Reads.CatalogCalls > 0);
        Assert.Equal(0, scenario.Host.Ingestion.Calls); AdmissionPrivacy(scenario);
    });

    /// <summary>Checks privacy independently of application storage spies, which a subsequent GET may intentionally reach.</summary>
    private static void AdmissionPrivacy(AdmissionScenario scenario)
    { SafeLogs(scenario.Host); Assert.All(scenario.Host.Http.Logs.Entries, entry => Assert.DoesNotContain(scenario.Counter, entry)); }

    /// <summary>Requires one positive integer Retry-After rather than accepting an arbitrary provider header.</summary>
    private static void PositiveRetry(HttpResponseMessage response) => Assert.True(int.Parse(Assert.Single(response.Headers.GetValues("Retry-After")), CultureInfo.InvariantCulture) > 0);

    /// <summary>Provides a semantically valid untrusted fake proposal without claiming actual model interpretation.</summary>
    private static StructuredQueryProposal AdmissionReady() => new("Ready", new(null, null, null, null, "displayName", "asc"), "ready");

    /// <summary>Preserves an assertion/operation failure alongside cleanup failures instead of letting finally hide it.</summary>
    private static async Task RunAndCleanupAsync(Func<Task> body, Func<Task> cleanup)
    {
        Exception? original = null;
        try { await body(); } catch (Exception error) { original = error; }
        try { await cleanup(); }
        catch (Exception error)
        {
            if (original is not null) throw new AggregateException("Test operation and cleanup failed.", original, error);
            throw;
        }
        if (original is not null) ExceptionDispatchInfo.Capture(original).Throw();
    }

    /// <summary>Owns explicit temporary counter provisioning and a controlled transport fact; never configures a production proxy.</summary>
    private sealed class AdmissionScenario : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "HealthMonitor-ai-http-" + Guid.NewGuid().ToString("N"));
        private AiQueryTestHost? host;
        private bool ownedDirectory;
        internal string Counter => Path.Combine(root, "counter.json");
        internal AdmissionClock Clock { get; } = new();
        internal IPAddress? Address { get; set; } = IPAddress.Parse("192.0.2.1");
        internal AiQueryTestHost Host => host ?? throw new InvalidOperationException("Test host has not started.");

        /// <summary>Includes initial provisioning and host startup in the cleanup boundary, even when setup is incomplete.</summary>
        internal static async Task RunAsync(Func<AdmissionScenario, Task> body, int count = 0, bool seed = true)
        {
            var scenario = new AdmissionScenario();
            await RunAndCleanupAsync(async () =>
            {
                if (Directory.Exists(scenario.root)) throw new InvalidOperationException("Test directory already exists.");
                Directory.CreateDirectory(scenario.root); scenario.ownedDirectory = true;
                if (seed) File.WriteAllText(scenario.Counter, JsonSerializer.Serialize(new { version = 1, day = "2026-10-18", count }));
                await scenario.StartAsync(); await body(scenario);
            }, () => scenario.DisposeAsync().AsTask());
        }
        /// <summary>Gets connection identity only from this controlled test fact at pipeline entry.</summary>
        private async Task StartAsync() => host = await AiQueryTestHost.StartWithAdmissionAsync(Counter, Clock, () => Address);
        /// <summary>Stops and drains the old owner before opening the same counter with a fresh process-policy lifetime.</summary>
        internal async Task RestartAsync() { await Host.DisposeAsync(); host = null; await StartAsync(); }
        /// <summary>Supplies a deliberately spoofed forwarding header; production admission must ignore it here.</summary>
        internal async Task<HttpResponseMessage> PostAsync(string forwarded)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, AiQueryTestHost.Route) { Content = new StringContent(Payload(Canary), Encoding.UTF8, "application/json") };
            request.Headers.Add("X-Forwarded-For", forwarded);
            return await Host.Http.Client.SendAsync(request);
        }
        /// <summary>Reads persisted reservation state independently of provider calls and response status.</summary>
        internal int Count() { using var json = JsonDocument.Parse(File.ReadAllText(Counter)); return json.RootElement.GetProperty("count").GetInt32(); }
        /// <summary>Reads the persisted rollover date independently of the controlled clock.</summary>
        internal string? Day() { using var json = JsonDocument.Parse(File.ReadAllText(Counter)); return json.RootElement.GetProperty("day").GetString(); }
        /// <summary>Drains the host before ownership cleanup; only this scenario's verified unique temporary directory can be deleted.</summary>
        public async ValueTask DisposeAsync()
        {
            await RunAndCleanupAsync(async () => { if (host is not null) await host.DisposeAsync(); }, () =>
            {
                // A nonterminating host fail-stops; an unexpected pre-drain error must likewise retain all test-owned state.
                if (host is not null && !host.Http.RequestsDrained)
                    throw new InvalidOperationException("Cannot clean a counter before the server has drained.");
                if (ownedDirectory)
                {
                    string resolved = Path.GetFullPath(root);
                    string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
                    if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                        || !Path.GetFileName(resolved).StartsWith("HealthMonitor-ai-http-", StringComparison.Ordinal))
                        throw new InvalidOperationException("Test cleanup path validation failed.");
                    Directory.Delete(resolved, recursive: true); ownedDirectory = false;
                }
                return Task.CompletedTask;
            });
        }
    }

    /// <summary>Shares a deterministic UTC instant between the real store and decorator; changes occur only between requests.</summary>
    private sealed class AdmissionClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => Now;
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
