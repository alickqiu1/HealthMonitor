using System.Net;
using System.Text.Json;
using HealthMonitor.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HealthMonitor.IntegrationTests.Api;

/// <summary>Seven real HTTP/Application/PostgreSQL scenarios confined to the verified disposable healthmonitor_test fixture.</summary>
/// <remarks>Listener/IP evidence remains fabricated. This is not full Program, real Kestrel, hosted/TLS, or independent concurrent Device-connection proof.</remarks>
[Collection(TelemetryIngestionDatabaseCollection.Name)]
public sealed class SimulationApiDatabaseTests
{
    private const string Canary = "after-save-failure-canary";

    /// <summary>Buffers and parses successful JSON, transferring document ownership while disposing the response.</summary>
    private static async Task<JsonDocument> JsonAsync(Task<HttpResponseMessage> pending)
    {
        using var response = await pending; Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Checks owned failures and diagnostics without reflecting SQL, provider details, or the injected failure marker.</summary>
    private static async Task FailureAsync(ReadApiDatabaseScenario test, HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        string text = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain(Canary, text);
        using var json = JsonDocument.Parse(text); Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
        Assert.All(test.Host.Logs.Entries, entry =>
        {
            Assert.DoesNotContain(Canary, entry); Assert.DoesNotContain("SELECT ", entry, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public Task Commit_PostThenGetPersistsSyntheticState() => ReadApiDatabaseScenario.RunAsync(async test =>
    {
        test.AdvanceClock(TimeSpan.FromSeconds(1)); Guid batch = await test.ReserveBatchAsync(); Guid device = test.Devices[0].Id;
        using var committed = await JsonAsync(test.PostAsync(batch, "HighLatency", device));
        Assert.Equal(batch, committed.RootElement.GetProperty("batchId").GetGuid());
        Assert.False(committed.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(1, committed.RootElement.GetProperty("affectedDeviceCount").GetInt32());
        Assert.Equal(test.Now, committed.RootElement.GetProperty("committedAtUtc").GetDateTimeOffset());
        Assert.EndsWith("Z", committed.RootElement.GetProperty("committedAtUtc").GetString());
        using var detail = await JsonAsync(test.GetAsync($"/api/v1/devices/{device}"));
        Assert.Equal("Degraded", detail.RootElement.GetProperty("health").GetString());
        Assert.Equal(600, detail.RootElement.GetProperty("latencyMs").GetInt32()); Assert.Equal(30m, detail.RootElement.GetProperty("cpuPercent").GetDecimal());
        Assert.Equal(test.Now, detail.RootElement.GetProperty("lastObservedAtUtc").GetDateTimeOffset());
        using var pings = await JsonAsync(test.GetAsync($"/api/v1/devices/{device}/telemetry?toUtc=" + Uri.EscapeDataString(test.Now.AddTicks(1).ToString("O"))));
        // The public ping DTO intentionally omits receipt identity; the controlled clock uniquely identifies the new observation.
        var persisted = Assert.Single(pings.RootElement.GetProperty("items").EnumerateArray(), ping =>
            ping.GetProperty("deviceId").GetGuid() == device && ping.GetProperty("observedAtUtc").GetDateTimeOffset() == test.Now);
        Assert.Equal("Success", persisted.GetProperty("outcome").GetString());
        Assert.Equal(600, persisted.GetProperty("latencyMs").GetInt32()); Assert.Equal(30m, persisted.GetProperty("cpuPercent").GetDecimal());
        using var incidents = await JsonAsync(test.GetAsync($"/api/v1/devices/{device}/incidents?state=open"));
        Assert.Equal("Latency", Assert.Single(incidents.RootElement.GetProperty("items").EnumerateArray()).GetProperty("kind").GetString());
        using var grid = await JsonAsync(test.GetAsync("/api/v1/devices?q=" + test.Marker + "&status=Degraded"));
        Assert.Equal(device, Assert.Single(grid.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        using var metrics = await JsonAsync(test.GetAsync("/api/v1/metrics/summary"));
        Assert.Equal(1, metrics.RootElement.GetProperty("degraded").GetInt32());
        Assert.Equal(320.5m, metrics.RootElement.GetProperty("averageLatencyMs").GetDecimal());
    }, enableSimulation: true);

    [Fact]
    public Task MatchingReplay_AfterRetirementAndExpiry_PreservesAllRows() => ReadApiDatabaseScenario.RunAsync(async test =>
    {
        test.AdvanceClock(TimeSpan.FromSeconds(1)); Guid batch = await test.ReserveBatchAsync(); Guid device = test.Devices[0].Id;
        using var original = await JsonAsync(test.PostAsync(batch, "Healthy", device));
        string snapshot = await test.TelemetrySnapshotAsync();
        await test.RetireOwnedDeviceAsync(0); test.AdvanceClock(TimeSpan.FromDays(1));
        using var replay = await JsonAsync(test.PostAsync(batch, "Healthy", device));
        Assert.True(replay.RootElement.GetProperty("replayed").GetBoolean());
        foreach (string field in new[] { "batchId", "affectedDeviceCount", "committedAtUtc" })
            Assert.Equal(original.RootElement.GetProperty(field).GetRawText(), replay.RootElement.GetProperty(field).GetRawText());
        Assert.Equal(snapshot, await test.TelemetrySnapshotAsync());
    }, enableSimulation: true);

    [Fact]
    public Task Conflict_409PreservesAllRows() => ReadApiDatabaseScenario.RunAsync(async test =>
    {
        test.AdvanceClock(TimeSpan.FromSeconds(1)); Guid batch = await test.ReserveBatchAsync(); Guid device = test.Devices[0].Id;
        using var original = await JsonAsync(test.PostAsync(batch, "Healthy", device));
        string snapshot = await test.TelemetrySnapshotAsync(); test.AdvanceClock(TimeSpan.FromDays(1));
        using var conflict = await test.PostAsync(batch, "HighCpu", device);
        await FailureAsync(test, conflict, HttpStatusCode.Conflict, "batch_id_conflict");
        Assert.Equal(snapshot, await test.TelemetrySnapshotAsync());
    }, enableSimulation: true);

    [Fact]
    public async Task IneligibleRegistration_400PreservesAllRows()
    {
        foreach (bool remove in new[] { false, true })
            await ReadApiDatabaseScenario.RunAsync(async test =>
            {
                test.AdvanceClock(TimeSpan.FromSeconds(1)); Guid batch = await test.ReserveBatchAsync();
                if (remove) await test.RemoveOwnedRegistrationAsync(0); else await test.RetireOwnedDeviceAsync(0);
                string snapshot = await test.TelemetrySnapshotAsync();
                using var response = await test.PostAsync(batch, "Healthy", test.Devices[0].Id);
                await FailureAsync(test, response, HttpStatusCode.BadRequest, "device_ineligible");
                Assert.Equal(snapshot, await test.TelemetrySnapshotAsync());
            }, enableSimulation: true);
    }

    [Fact]
    public Task PostSaveFailure_500RollsBackAllRows() => ReadApiDatabaseScenario.RunAsync(async test =>
    {
        test.AdvanceClock(TimeSpan.FromSeconds(1)); Guid batch = await test.ReserveBatchAsync();
        string snapshot = await test.TelemetrySnapshotAsync(); var failure = new FailAfterExecutedSave();
        test.TelemetryInterceptors.Add(failure);
        using var response = await test.PostAsync(batch, "HighLatency", test.Devices[0].Id);
        await FailureAsync(test, response, HttpStatusCode.InternalServerError, "unexpected_error");
        Assert.True(failure.ExecutedRows > 0); Assert.Equal(snapshot, await test.TelemetrySnapshotAsync());
    }, enableSimulation: true);

    [Fact]
    public Task RateLimitedWrite_NoPersistenceOrCommands() => ReadApiDatabaseScenario.RunAsync(async test =>
    {
        for (int index = 0; index < 5; index++)
        {
            test.AdvanceClock(TimeSpan.FromSeconds(1)); Guid acceptedBatch = await test.ReserveBatchAsync();
            using var accepted = await JsonAsync(test.PostAsync(acceptedBatch, "Healthy", test.Devices[0].Id));
        }
        string snapshot = await test.TelemetrySnapshotAsync(); Guid rejectedBatch = await test.ReserveBatchAsync();
        int commands = test.Probe.Count;
        using var rejected = await test.PostAsync(rejectedBatch, "HighCpu", test.Devices[0].Id);
        await FailureAsync(test, rejected, HttpStatusCode.TooManyRequests, "simulation_rate_limited");
        Assert.True(rejected.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.Equal(commands, test.Probe.Count); Assert.Equal(snapshot, await test.TelemetrySnapshotAsync());
    }, enableSimulation: true);

    [Fact]
    public Task DisabledSimulation_NoPersistenceOrCommands() => ReadApiDatabaseScenario.RunAsync(async test =>
    {
        Guid batch = await test.ReserveBatchAsync(); string snapshot = await test.TelemetrySnapshotAsync();
        Assert.Equal(0, test.Probe.Count);
        using var rejected = await test.PostAsync(batch, "Healthy", test.Devices[0].Id);
        await FailureAsync(test, rejected, HttpStatusCode.NotFound, "not_found");
        Assert.Equal(0, test.Probe.Count); Assert.Equal(snapshot, await test.TelemetrySnapshotAsync());
    });

    /// <summary>Throws after real SQL SaveChanges completed but before the explicit ingestion transaction commits.</summary>
    /// <remarks>Confined to request Telemetry contexts. It is neither a network outage nor deferred Device provisioning mid-insert injection.</remarks>
    private sealed class FailAfterExecutedSave : SaveChangesInterceptor
    {
        internal int ExecutedRows { get; private set; }
        /// <inheritdoc />
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        { ExecutedRows = result; throw new InvalidOperationException(Canary); }
    }
}
