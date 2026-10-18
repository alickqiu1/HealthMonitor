using System.Net;
using System.Text.Json;
using HealthMonitor.IntegrationTests.Infrastructure;

namespace HealthMonitor.IntegrationTests.Api;

/// <summary>Eight real HTTP/Device/Telemetry/probe cases on the verified disposable database; no local execution is implied by compilation.</summary>
/// <remarks>Feature-only factory; sequential externally owned Device rollback transaction, independent Telemetry query scopes, no full Program/workers/HTTPS proof.</remarks>
[Collection(TelemetryIngestionDatabaseCollection.Name)]
public sealed class ReadApiDatabaseTests
{
    /// <summary>Gets and buffers successful JSON through the real feature, transferring document ownership to the assertion scope.</summary>
    private static async Task<JsonDocument> GetAsync(ReadApiDatabaseScenario test, string path)
    {
        using var response = await test.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
    /// <summary>Encodes explicit UTC endpoints, preserving fractional ticks for the real PostgreSQL comparison tests.</summary>
    private static string Stamp(DateTimeOffset value) => Uri.EscapeDataString(value.UtcDateTime.ToString("O"));
    /// <summary>Checks wire UTC spelling as well as timestamp value.</summary>
    private static void Utc(JsonElement value, DateTimeOffset expected)
    {
        Assert.EndsWith("Z", value.GetString()); Assert.Equal(expected, value.GetDateTimeOffset());
    }

    [Fact]
    public Task GridAndMetrics_ReadRealCatalogAndTelemetry() => ReadApiDatabaseScenario.RunAsync(async test =>
    {
        using var grid = await GetAsync(test, "/api/v1/devices?q=" + test.Marker + "&sort=status");
        var rows = grid.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, grid.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(test.Devices.Select(d => d.Id), rows.Select(r => r.GetProperty("id").GetGuid()));
        Assert.Equal("Degraded", rows[0].GetProperty("health").GetString());
        Assert.Equal("Healthy", rows[1].GetProperty("health").GetString());
        Assert.Equal(95m, rows[0].GetProperty("cpuPercent").GetDecimal());
        Utc(rows[0].GetProperty("lastObservedAtUtc"), test.Now); Utc(grid.RootElement.GetProperty("asOfUtc"), test.Now);
        using var metrics = await GetAsync(test, "/api/v1/metrics/summary");
        var m = metrics.RootElement;
        Assert.Equal(test.BaselineActive + 2, m.GetProperty("total").GetInt32());
        Assert.Equal(test.BaselineActive, m.GetProperty("unknown").GetInt32());
        Assert.Equal(1, m.GetProperty("healthy").GetInt32()); Assert.Equal(1, m.GetProperty("degraded").GetInt32());
        Assert.Equal(0, m.GetProperty("offline").GetInt32()); Assert.Equal(40.5m, m.GetProperty("averageLatencyMs").GetDecimal());
        Assert.Equal(2, m.GetProperty("sampleCount").GetInt32()); Utc(m.GetProperty("asOfUtc"), test.Now);
        Assert.All(test.Host.Logs.Entries, entry => Assert.DoesNotContain("SELECT ", entry, StringComparison.OrdinalIgnoreCase));
    });

    [Fact]
    public Task Grid_GlobalFiltersOrderingAndPagingUseRealRows() => ReadApiDatabaseScenario.RunAsync(async test =>
    {
        string prefix = "/api/v1/devices?q=" + test.Marker.ToLowerInvariant();
        using var first = await GetAsync(test, prefix + "&site=den&sort=latencyMs&direction=desc&pageSize=1");
        Assert.Equal(test.Devices[1].Id, Assert.Single(first.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(2, first.RootElement.GetProperty("totalCount").GetInt64());
        using var second = await GetAsync(test, prefix + "&site=DEN&sort=latencyMs&direction=desc&pageSize=1&page=2");
        Assert.Equal(test.Devices[0].Id, Assert.Single(second.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        using var filtered = await GetAsync(test, prefix + "&type=database&status=Healthy");
        Assert.Equal(test.Devices[1].Id, Assert.Single(filtered.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(1, filtered.RootElement.GetProperty("totalCount").GetInt64());
        using var beyond = await GetAsync(test, prefix + "&page=2147483647&pageSize=100");
        Assert.Empty(beyond.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(2, beyond.RootElement.GetProperty("totalCount").GetInt64());
    });

    [Fact]
    public Task Detail_RetiredAndUnknownUseRealCatalog() => ReadApiDatabaseScenario.RunAsync(async test =>
    {
        var retired = test.Devices[1];
        using var detail = await GetAsync(test, "/api/v1/devices/" + retired.Id);
        var d = detail.RootElement;
        Assert.Equal(retired.Id, d.GetProperty("id").GetGuid());
        Assert.Equal(retired.AssetTag, d.GetProperty("assetTag").GetString());
        Utc(d.GetProperty("createdAtUtc"), retired.CreatedAtUtc);
        Utc(d.GetProperty("retiredAtUtc"), retired.RetiredAtUtc!.Value);
        Utc(d.GetProperty("lastSuccessfulAtUtc"), test.Now.AddDays(-1).AddMinutes(-1));
        Assert.Equal("Offline", d.GetProperty("health").GetString());
        using var grid = await GetAsync(test, "/api/v1/devices?q=" + test.Marker);
        Assert.Equal(test.Devices[0].Id, Assert.Single(grid.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        foreach (var suffix in new[] { "", "/telemetry", "/incidents" })
        {
            using var response = await test.GetAsync("/api/v1/devices/" + test.UnknownId + suffix);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("not_found", error.RootElement.GetProperty("code").GetString());
        }
    }, retireSecond: true);

    [Fact]
    public Task Telemetry_HalfOpenRangeAndPagingThroughHttp() => ReadApiDatabaseScenario.RunAsync(async test =>
    {
        string path = $"/api/v1/devices/{test.Devices[0].Id}/telemetry";
        string range = "?fromUtc=" + Stamp(test.Now.AddHours(-2)) + "&toUtc=" + Stamp(test.Now) + "&pageSize=1";
        using var first = await GetAsync(test, path + range);
        var timeout = Assert.Single(first.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(2, first.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal("Timeout", timeout.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, timeout.GetProperty("latencyMs").ValueKind);
        Assert.Equal(JsonValueKind.Null, timeout.GetProperty("cpuPercent").ValueKind);
        Utc(timeout.GetProperty("observedAtUtc"), test.Now.AddHours(-1));
        using var second = await GetAsync(test, path + range + "&page=2");
        Assert.Equal(600, Assert.Single(second.RootElement.GetProperty("items").EnumerateArray()).GetProperty("latencyMs").GetInt32());
        using var fractional = await GetAsync(test, path + "?fromUtc=" + Stamp(test.Now.AddHours(-1).AddTicks(1)) + "&toUtc=" + Stamp(test.Now.AddTicks(1)));
        Assert.Equal(1, fractional.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(40, Assert.Single(fractional.RootElement.GetProperty("items").EnumerateArray()).GetProperty("latencyMs").GetInt32());
        using var empty = await GetAsync(test, path + "?fromUtc=" + Stamp(test.Now.AddTicks(1)) + "&toUtc=" + Stamp(test.Now.AddTicks(2)));
        Assert.Empty(empty.RootElement.GetProperty("items").EnumerateArray()); Assert.Equal(0, empty.RootElement.GetProperty("totalCount").GetInt64());
        Utc(empty.RootElement.GetProperty("asOfUtc"), test.Now);
    });

    [Fact]
    public Task Incidents_OpenResolvedAllAndPagingThroughHttp() => ReadApiDatabaseScenario.RunAsync(async test =>
    {
        string path = $"/api/v1/devices/{test.Devices[0].Id}/incidents";
        using var all = await GetAsync(test, path);
        Assert.Equal(2, all.RootElement.GetProperty("totalCount").GetInt64());
        using var noIncidents = await GetAsync(test, $"/api/v1/devices/{test.Devices[1].Id}/incidents");
        Assert.Empty(noIncidents.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(0, noIncidents.RootElement.GetProperty("totalCount").GetInt64());
        var allRows = all.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(allRows.Select(i => i.GetProperty("id").GetGuid()).OrderDescending(), allRows.Select(i => i.GetProperty("id").GetGuid()));
        using var open = await GetAsync(test, path + "?state=open&pageSize=1");
        var active = Assert.Single(open.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("Cpu", active.GetProperty("kind").GetString()); Assert.Equal(JsonValueKind.Null, active.GetProperty("resolvedAtUtc").ValueKind);
        using var resolved = await GetAsync(test, path + "?state=resolved");
        var closed = Assert.Single(resolved.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("Latency", closed.GetProperty("kind").GetString()); Utc(closed.GetProperty("resolvedAtUtc"), test.Now);
        using var beyond = await GetAsync(test, path + "?state=resolved&page=2&pageSize=1");
        Assert.Empty(beyond.RootElement.GetProperty("items").EnumerateArray()); Assert.Equal(1, beyond.RootElement.GetProperty("totalCount").GetInt64());
    });

    [Fact]
    public Task Validation_RejectedBeforeDatabaseCommands() => ReadApiDatabaseScenario.RunAsync(async test =>
    {
        Interlocked.Exchange(ref test.Probe.Count, 0);
        foreach (var path in new[]
        {
            "/api/v1/devices?page=0", "/api/v1/devices?sort=notAllowed", "/api/v1/devices?page=1&PAGE=2",
            "/api/v1/devices?site=", "/api/v1/devices/not-a-uuid",
            $"/api/v1/devices/{test.Devices[0].Id}/telemetry?fromUtc=2026-10-18T12:00:00",
            $"/api/v1/devices/{test.Devices[0].Id}/telemetry?fromUtc={Stamp(test.Now)}&toUtc={Stamp(test.Now)}",
            $"/api/v1/devices/{test.Devices[0].Id}/incidents?fromUtc={Stamp(test.Now)}",
            "/api/v1/metrics/summary?status=Healthy"
        })
        {
            using var response = await test.GetAsync(path);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal(0, test.Probe.Count);
    });

    [Fact]
    public Task Readiness_VerifiesFixtureDatabaseAndLivenessStaysIndependent() => ReadApiDatabaseScenario.RunAsync(async test =>
    {
        using var live = await GetAsync(test, "/health/live");
        Assert.Equal("alive", live.RootElement.GetProperty("status").GetString());
        using var ready = await GetAsync(test, "/health/ready");
        Assert.Equal("ready", ready.RootElement.GetProperty("status").GetString());
        // The registered production check uses SELECT 1 on the same independently fixture-verified pool.
        // This is connectivity proof only, not schema/role/TLS/deployment verification.
    });
    [Fact]
    public Task ProviderFailure_ReturnsSanitizedProblemThroughRealContext() => ReadApiDatabaseScenario.RunAsync(async test =>
    {
        const string canary = "db-transport-canary-42";
        test.Probe.Failure = new Npgsql.NpgsqlException(canary, new IOException(canary));
        using var response = await test.GetAsync("/api/v1/devices");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(canary, text);
        using var json = JsonDocument.Parse(text);
        Assert.Equal("database_unavailable", json.RootElement.GetProperty("code").GetString());
        Assert.True(test.Probe.Count > 0);
        Assert.All(test.Host.Logs.Entries, entry =>
        {
            Assert.DoesNotContain(canary, entry);
            Assert.DoesNotContain("SELECT ", entry, StringComparison.OrdinalIgnoreCase);
        });
        // This injects at a real EF command boundary; it does not reproduce an actual network/server outage.
    });
}
