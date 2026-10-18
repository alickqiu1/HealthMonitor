using System.Net;
using System.Text;
using System.Text.Json;
using HealthMonitor.Application.Telemetry;
using HealthMonitor.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace HealthMonitor.IntegrationTests.Api;

/// <summary>Ten database-free cases through real MVC, conditional discovery, local access, body limits, CORS, and rate limiting.</summary>
/// <remarks>Ingestion and effective listener facts are explicit doubles; PostgreSQL commit/replay and real Kestrel/hosted security are separate gates.</remarks>
public sealed class SimulationApiContractTests
{
    private const string Canary = "credential-canary-42";
    /// <summary>Creates only the three supported wire fields, preserving the caller identity on explicit retry.</summary>
    private static string Payload(Guid? batch = null, string scenario = "Healthy", Guid[]? devices = null) =>
        JsonSerializer.Serialize(new { batchId = batch ?? Guid.CreateVersion7(), deviceIds = devices ?? new[] { Guid.NewGuid() }, scenario });

    /// <summary>Verifies public failure shape and the complete captured diagnostic stream contain no injected secret marker.</summary>
    private static async Task Problem(SimulationTestHost host, HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain(Canary, text);
        using var json = JsonDocument.Parse(text);
        Assert.Equal((int)expected, json.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("code").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
        Assert.All(host.Http.Logs.Entries, entry => Assert.DoesNotContain(Canary, entry));
    }

    [Fact]
    public async Task DefaultDisabled_RouteAbsent()
    {
        foreach (string environment in new[] { "Development", "Production", "Testing" })
        {
            await using var host = await SimulationTestHost.StartAsync(false, false, environment);
            using var response = await host.PostAsync(Payload()); await Problem(host, response, HttpStatusCode.NotFound);
            Assert.Empty(host.Ingestion.Requests);
            var actions = host.Http.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items;
            Assert.DoesNotContain(actions, item => item.DisplayName?.Contains("SimulationController", StringComparison.Ordinal) == true);
        }
    }

    [Fact]
    public async Task LocalAccess_AllEffectiveBindingAndIpGates()
    {
        await using var host = await SimulationTestHost.StartAsync();
        foreach (var bindings in new string[]?[] { null, Array.Empty<string>(), new[] { "http://0.0.0.0:5000" },
            new[] { "http://localhost:5000", "http://[::]:5000" }, new[] { "http://example.com:5000" } })
        {
            host.SetBindings(bindings);
            using var response = await host.PostAsync(Payload()); await Problem(host, response, HttpStatusCode.Forbidden);
        }
        host.SetBindings("http://localhost:5000");
        foreach (var (local, remote) in new (string?, string?)[] { (null, "127.0.0.1"), ("127.0.0.1", null), ("10.0.0.2", "127.0.0.1"), ("127.0.0.1", "10.0.0.2") })
        {
            host.Network.Local = local is null ? null : IPAddress.Parse(local);
            host.Network.Remote = remote is null ? null : IPAddress.Parse(remote);
            using var response = await host.PostAsync(Payload()); await Problem(host, response, HttpStatusCode.Forbidden);
        }
        Assert.Empty(host.Ingestion.Requests);
        host.Network.Local = IPAddress.IPv6Loopback; host.Network.Remote = IPAddress.Parse("::ffff:127.0.0.1");
        host.SetBindings("http://127.0.0.1:5000", "https://[::1]:5001");
        using var allowed = await host.PostAsync(Payload()); Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Single(host.Ingestion.Requests);
    }

    [Fact]
    public async Task CommitAndSameIdReplay_WireWaitsForService()
    {
        await using var host = await SimulationTestHost.StartAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledgment = new TaskCompletionSource<TelemetryIngestionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid batch = Guid.CreateVersion7(); string body = Payload(batch);
        var saved = new TelemetryIngestionResult(batch, 1, host.Ingestion.CommittedAt, false);
        host.Ingestion.Behavior = async (_, token) => { entered.TrySetResult(); return await acknowledgment.Task.WaitAsync(token); };
        var pending = host.PostAsync(body);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(pending.IsCompleted);
        acknowledgment.SetResult(saved);
        using var response = await pending; Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "affectedDeviceCount", "batchId", "committedAtUtc", "replayed" }, json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(batch, json.RootElement.GetProperty("batchId").GetGuid()); Assert.False(json.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(1, json.RootElement.GetProperty("affectedDeviceCount").GetInt32()); Assert.EndsWith("Z", json.RootElement.GetProperty("committedAtUtc").GetString());
        host.Ingestion.Behavior = (_, _) => Task.FromResult(saved with { Replayed = true });
        using var replay = await host.PostAsync(body); Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync()); Assert.True(replayJson.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(2, host.Ingestion.Requests.Count); Assert.All(host.Ingestion.Requests, request => Assert.Equal(batch, request.BatchId));
    }

    [Fact]
    public async Task InvalidShapes_RejectBeforeIngestion()
    {
        Guid batch = Guid.CreateVersion7(); Guid device = Guid.NewGuid(); string valid = Payload(batch, devices: new[] { device });
        string[] invalid = { "null", "[]", "{}", "{", valid.Replace(batch.ToString(), Canary), Payload(Guid.Empty), Payload(Guid.NewGuid()),
            Payload(scenario: "healthy"), valid.Replace("\"Healthy\"", "1"), Payload(devices: new[] { Guid.Empty }),
            Payload(devices: new[] { device, device }), Payload(devices: Enumerable.Range(0, 26).Select(_ => Guid.NewGuid()).ToArray()),
            valid.Replace("\"scenario\":\"Healthy\"", "\"scenario\":\"Healthy\",\"scenario\":\"Timeout\""),
            valid.Replace("\"scenario\":\"Healthy\"", "\"scenario\":\"Healthy\",\"Scenario\":\"Timeout\""),
            valid[..^1] + ",\"observedAtUtc\":\"" + Canary + "\"}",
            JsonSerializer.Serialize(new { batchId = batch, deviceIds = Array.Empty<Guid>(), scenario = "Healthy" }),
            JsonSerializer.Serialize(new { batchId = batch, deviceIds = (Guid[]?)null, scenario = "Healthy" }) };
        foreach (string body in invalid)
        {
            // A fresh host preserves the actual production quota instead of weakening the limiter for validation tests.
            await using var host = await SimulationTestHost.StartAsync();
            using var response = await host.PostAsync(body); await Problem(host, response, HttpStatusCode.BadRequest);
            Assert.Empty(host.Ingestion.Requests);
        }
        await using var boundaryHost = await SimulationTestHost.StartAsync();
        Guid[] selected = Enumerable.Range(0, 25).Select(_ => Guid.NewGuid()).ToArray();
        using var accepted = await boundaryHost.PostAsync(Payload(devices: selected));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var acceptedJson = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        Assert.Equal(25, acceptedJson.RootElement.GetProperty("affectedDeviceCount").GetInt32());
        Assert.Equal(selected, Assert.Single(boundaryHost.Ingestion.Requests).DeviceIds);
    }

    [Fact]
    public async Task BodyLimit_ExactAndKnownOrChunkedOverflow()
    {
        await using var host = await SimulationTestHost.StartAsync(); string json = Payload();
        string exact = json + new string(' ', 16384 - Encoding.UTF8.GetByteCount(json));
        using var known = await host.PostAsync(exact); Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        using var overflow = await host.PostAsync(exact + " "); await Problem(host, overflow, HttpStatusCode.RequestEntityTooLarge);
        using var streamed = await host.PostAsync(exact + " ", chunked: true); await Problem(host, streamed, HttpStatusCode.RequestEntityTooLarge);
        using var streamedExact = await host.PostAsync(exact, chunked: true); Assert.Equal(HttpStatusCode.OK, streamedExact.StatusCode);
        string utf8Overflow = json.Replace("\"Healthy\"", "\"" + new string('é', 8300) + "\"");
        Assert.True(utf8Overflow.Length < 16384);
        using var utf8 = await host.PostAsync(utf8Overflow, chunked: true); await Problem(host, utf8, HttpStatusCode.RequestEntityTooLarge);
        Assert.Equal(2, host.Ingestion.Requests.Count);
    }

    [Fact]
    public async Task RateLimit_FiveThen429_DenialsDoNotConsumeQuota()
    {
        await using var host = await SimulationTestHost.StartAsync(); host.Network.Remote = IPAddress.Parse("10.0.0.2");
        using var denied = await host.PostAsync(Payload()); await Problem(host, denied, HttpStatusCode.Forbidden);
        host.Network.Remote = IPAddress.Loopback;
        for (int index = 0; index < 5; index++) { using var accepted = await host.PostAsync(Payload()); Assert.Equal(HttpStatusCode.OK, accepted.StatusCode); }
        using var rejected = await host.PostAsync(Payload()); await Problem(host, rejected, HttpStatusCode.TooManyRequests);
        Assert.True(rejected.Headers.RetryAfter?.Delta > TimeSpan.Zero); Assert.Equal(5, host.Ingestion.Requests.Count);
    }

    [Fact]
    public async Task OwnedErrors_SanitizedStableStatuses()
    {
        foreach (var (error, expected, code) in new (Exception, HttpStatusCode, string)[]
        {
            (new TelemetryIngestionConflictException(), HttpStatusCode.Conflict, "batch_id_conflict"),
            (new TelemetryDeviceEligibilityException(), HttpStatusCode.BadRequest, "device_ineligible"),
            (new TelemetryBatchIdValidationException("batch_id_expired", Canary), HttpStatusCode.BadRequest, "batch_id_expired"),
            (new TelemetryBatchIdValidationException("batch_id_future", Canary), HttpStatusCode.BadRequest, "batch_id_future"),
            (new TelemetryIngestionBusyException(), HttpStatusCode.ServiceUnavailable, "simulation_busy")
        })
        {
            await using var host = await SimulationTestHost.StartAsync(); host.Ingestion.Behavior = (_, _) => Task.FromException<TelemetryIngestionResult>(error);
            using var response = await host.PostAsync(Payload()); await Problem(host, response, expected);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
            if (error is TelemetryIngestionBusyException) Assert.Equal("2", Assert.Single(response.Headers.GetValues("Retry-After")));
        }
    }

    [Fact]
    public async Task CancellationAndUnexpectedFailures_NotValidation()
    {
        foreach (Exception error in new Exception[] { new ArgumentException(Canary), new InvalidOperationException(Canary),
            new OperationCanceledException(Canary), new TelemetryBatchIdValidationException(Canary, Canary) })
        {
            await using var host = await SimulationTestHost.StartAsync(); host.Ingestion.Behavior = (_, _) => Task.FromException<TelemetryIngestionResult>(error);
            using var response = await host.PostAsync(Payload()); await Problem(host, response, HttpStatusCode.InternalServerError);
        }
        await using var canceledHost = await SimulationTestHost.StartAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        canceledHost.Ingestion.Behavior = async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
            throw new InvalidOperationException("Unreachable");
        };
        using var abort = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, SimulationTestHost.Route) { Content = new StringContent(Payload(), Encoding.UTF8, "application/json") };
        var pending = canceledHost.Http.Client.SendAsync(request, abort.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Cors_PostPolicySeparateFromPublicReads()
    {
        foreach (bool enabled in new[] { false, true })
        {
            await using var host = await SimulationTestHost.StartAsync(enabled, enabled);
            using var preflight = new HttpRequestMessage(HttpMethod.Options, SimulationTestHost.Route);
            preflight.Headers.Add("Origin", "http://localhost:5173"); preflight.Headers.Add("Access-Control-Request-Method", "POST");
            preflight.Headers.Add("Access-Control-Request-Headers", "Content-Type");
            using var response = await host.Http.Client.SendAsync(preflight);
            bool permitsPost = response.Headers.TryGetValues("Access-Control-Allow-Methods", out var methods)
                && methods.SelectMany(value => value.Split(',')).Any(value => value.Trim() == "POST");
            Assert.Equal(enabled, permitsPost); Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
            if (enabled) Assert.Equal("http://localhost:5173", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
            using var readPreflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/devices");
            readPreflight.Headers.Add("Origin", "http://localhost:5173"); readPreflight.Headers.Add("Access-Control-Request-Method", "GET");
            using var readResponse = await host.Http.Client.SendAsync(readPreflight);
            Assert.Equal("GET", Assert.Single(readResponse.Headers.GetValues("Access-Control-Allow-Methods")));
            Assert.Empty(host.Ingestion.Requests);
        }
    }

    [Fact]
    public async Task UnsupportedContentTypeAndUnknownQuery_RejectBeforeIngestion()
    {
        await using var host = await SimulationTestHost.StartAsync();
        using var text = await host.PostAsync(Payload(), mediaType: "text/plain"); await Problem(host, text, HttpStatusCode.UnsupportedMediaType);
        using var query = await host.PostAsync(Payload(), query: "?measurement=" + Canary); await Problem(host, query, HttpStatusCode.BadRequest);
        using var missingType = new HttpRequestMessage(HttpMethod.Post, SimulationTestHost.Route) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Payload())) };
        using var missingTypeResponse = await host.Http.Client.SendAsync(missingType); await Problem(host, missingTypeResponse, HttpStatusCode.UnsupportedMediaType);
        Assert.Empty(host.Ingestion.Requests);
    }
}
