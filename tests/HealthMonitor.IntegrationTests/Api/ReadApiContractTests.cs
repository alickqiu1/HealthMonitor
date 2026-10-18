using System.Net;
using System.Text.Json;
using HealthMonitor.Api.Composition;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using HealthMonitor.Infrastructure.Data.DeviceContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using HealthMonitor.Application.Device;
using HealthMonitor.Application.IntegrationContracts;
using HealthMonitor.Domain.Telemetry;
using HealthMonitor.IntegrationTests.Infrastructure;

namespace HealthMonitor.IntegrationTests.Api;

/// <summary>Database-free HTTP integration through the shared production read pipeline and explicit fake application ports.</summary>
/// <remarks>These fifteen cases prove transport/composition, not PostgreSQL, full Program startup, HTTPS, workers, or deployment.</remarks>
public sealed class ReadApiContractTests
{
    private const string Canary = "credential-canary-42";
    private static readonly string[] RowFields = { "id", "assetTag", "displayName", "deviceType", "siteCode", "health", "lastObservedAtUtc", "latencyMs", "cpuPercent" };

    /// <summary>Gets a successful JSON response and transfers document ownership to the test.</summary>
    private static async Task<JsonDocument> GetAsync(ReadApiTestHost host, string path)
    {
        using var response = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Asserts exact wire keys so internal properties cannot accidentally expand the public contract.</summary>
    private static void Fields(JsonElement element, params string[] expected) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal), element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

    /// <summary>Checks the UTC Z contract independently of serializer fractional-second formatting.</summary>
    private static void Utc(JsonElement element, DateTimeOffset expected)
    {
        Assert.EndsWith("Z", element.GetString());
        Assert.Equal(expected, element.GetDateTimeOffset());
    }

    /// <summary>Requires a safe problem response and checks every captured log, including any attached exception.</summary>
    private static async Task ProblemAsync(ReadApiTestHost host, string path, HttpStatusCode expected)
    {
        using var response = await host.Client.GetAsync(path);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Canary, text);
        using var document = JsonDocument.Parse(text);
        Assert.Equal((int)expected, document.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("code").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("traceId").GetString()));
        Assert.All(host.Logs.Entries, entry => Assert.DoesNotContain(Canary, entry));
    }

    /// <summary>Encodes explicit UTC input, including plus signs which query-string parsing otherwise treats as spaces.</summary>
    private static string Stamp(DateTimeOffset value, bool z = true) =>
        Uri.EscapeDataString(z ? value.UtcDateTime.ToString("O") : value.ToString("O"));

    [Fact]
    public async Task Grid_DefaultsSerializeExactV1ShapeAndNullMeasurements()
    {
        var ports = new FakeReadPorts(); var device = ports.AddDevice(1);
        await using var host = await ReadApiTestHost.StartAsync(ports);
        using var json = await GetAsync(host, "/api/v1/devices?q=%20%20");
        var root = json.RootElement;
        Fields(root, "items", "page", "pageSize", "totalCount", "asOfUtc");
        Assert.Equal(1, root.GetProperty("page").GetInt32()); Assert.Equal(25, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, root.GetProperty("totalCount").GetInt64()); Utc(root.GetProperty("asOfUtc"), ports.Now);
        var row = Assert.Single(root.GetProperty("items").EnumerateArray()); Fields(row, RowFields);
        Assert.Equal(device.DeviceId, row.GetProperty("id").GetGuid());
        Assert.Equal("Unknown", row.GetProperty("health").GetString());
        foreach (var name in new[] { "lastObservedAtUtc", "latencyMs", "cpuPercent" }) Assert.Equal(JsonValueKind.Null, row.GetProperty(name).ValueKind);
        Assert.Equal(1, ports.ClockReads);
    }

    [Fact]
    public async Task Grid_FilterSortPageUsesGlobalSnapshotAndStableTies()
    {
        var ports = new FakeReadPorts();
        for (int i = 1; i <= 30; i++) ports.AddDevice(i);
        await using var host = await ReadApiTestHost.StartAsync(ports);
        using var found = await GetAsync(host, "/api/v1/devices?q=device%2030&site=den&type=server&status=Unknown&sort=displayName&direction=desc&pageSize=1");
        Assert.Equal(ports.Devices[29].DeviceId, Assert.Single(found.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(1, found.RootElement.GetProperty("totalCount").GetInt64());
        using var tied = await GetAsync(host, "/api/v1/devices?sort=latencyMs&direction=desc&page=2&pageSize=2");
        Assert.Equal(30, tied.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(new[] { ports.Devices[2].DeviceId, ports.Devices[3].DeviceId },
            tied.RootElement.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("id").GetGuid()));
        using var beyond = await GetAsync(host, "/api/v1/devices?page=2147483647&pageSize=100");
        Assert.Empty(beyond.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(30, beyond.RootElement.GetProperty("totalCount").GetInt64());
    }

    [Fact]
    public async Task QueryAllowlist_RejectsUnknownDuplicateCaseVariantsAndBlankTokens()
    {
        var ports = new FakeReadPorts(); var device = ports.AddDevice(1);
        await using var host = await ReadApiTestHost.StartAsync(ports);
        var paths = new[]
        {
            "/api/v1/devices?unknown=" + Canary,
            "/api/v1/devices?page=1&page=2",
            "/api/v1/devices?Page=1&page=2",
            "/api/v1/devices?q=one&Q=two",
            "/api/v1/devices?site=", "/api/v1/devices?type=%20", "/api/v1/devices?status=",
            "/api/v1/devices?sort=", "/api/v1/devices?direction=%20",
            "/api/v1/devices?status=0", "/api/v1/devices?sort=id", "/api/v1/devices?site=LON",
            $"/api/v1/devices/{device.DeviceId}?q=x",
            $"/api/v1/devices/{device.DeviceId}/telemetry?state=open",
            $"/api/v1/devices/{device.DeviceId}/telemetry?fromUtc=",
            $"/api/v1/devices/{device.DeviceId}/incidents?state=",
            $"/api/v1/devices/{device.DeviceId}/incidents?fromUtc={Stamp(ports.Now)}",
            "/api/v1/metrics/summary?site=DEN"
        };
        foreach (var path in paths) await ProblemAsync(host, path, HttpStatusCode.BadRequest);
        Assert.Equal(0, ports.CatalogCalls); Assert.Equal(0, ports.HistoryCalls); Assert.Equal(0, ports.TelemetryCalls);
    }

    [Fact]
    public async Task BindingErrors_MalformedGuidPagingAndDatesAreSanitized()
    {
        var ports = new FakeReadPorts(); var device = ports.AddDevice(1);
        await using var host = await ReadApiTestHost.StartAsync(ports);
        foreach (var path in new[]
        {
            "/api/v1/devices?page=" + Canary, "/api/v1/devices?page=0", "/api/v1/devices?pageSize=101",
            "/api/v1/devices/" + Canary,
            $"/api/v1/devices/{device.DeviceId}/telemetry?pageSize=0",
            $"/api/v1/devices/{device.DeviceId}/telemetry?fromUtc={Canary}",
            $"/api/v1/devices/{device.DeviceId}/telemetry?fromUtc=2026-10-18T11:00:00",
            $"/api/v1/devices/{device.DeviceId}/telemetry?fromUtc=2026-10-18T11:00:00%2B01:00"
        }) await ProblemAsync(host, path, HttpStatusCode.BadRequest);
        Assert.Equal(0, ports.CatalogCalls); Assert.Equal(0, ports.ClockReads);
    }

    [Fact]
    public async Task Detail_KnownRetiredAndUnknownFollowReadContracts()
    {
        var ports = new FakeReadPorts(); var retired = ports.AddDevice(1, retired: true); var active = ports.AddDevice(2);
        var observed = ports.Now.AddDays(-1); var lastSuccess = observed.AddMinutes(-1);
        ports.States.Add(new(retired.DeviceId, observed, lastSuccess, PingOutcome.Timeout, null, null));
        await using var host = await ReadApiTestHost.StartAsync(ports);
        using var json = await GetAsync(host, "/api/v1/devices/" + retired.DeviceId);
        var root = json.RootElement; Fields(root, RowFields.Concat(new[] { "createdAtUtc", "retiredAtUtc", "lastSuccessfulAtUtc", "asOfUtc" }).ToArray());
        Assert.Equal("Offline", root.GetProperty("health").GetString());
        Utc(root.GetProperty("createdAtUtc"), ports.Now.AddDays(-2)); Utc(root.GetProperty("retiredAtUtc"), ports.Now.AddDays(-1));
        Utc(root.GetProperty("lastSuccessfulAtUtc"), lastSuccess); Utc(root.GetProperty("lastObservedAtUtc"), observed); Utc(root.GetProperty("asOfUtc"), ports.Now);
        using var unknownState = await GetAsync(host, "/api/v1/devices/" + active.DeviceId);
        Assert.Equal("Unknown", unknownState.RootElement.GetProperty("health").GetString());
        Assert.Equal(JsonValueKind.Null, unknownState.RootElement.GetProperty("retiredAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, unknownState.RootElement.GetProperty("lastSuccessfulAtUtc").ValueKind);
        await ProblemAsync(host, "/api/v1/devices/" + Guid.NewGuid(), HttpStatusCode.NotFound);
        await ProblemAsync(host, "/api/v1/devices/" + Guid.Empty, HttpStatusCode.NotFound);
        await ProblemAsync(host, "/api/v1/" + Canary, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Telemetry_DefaultPartialRangesAndBoundsUseOneClock()
    {
        var ports = new FakeReadPorts(); var device = ports.AddDevice(1);
        ports.Pings.Add(new(Guid.NewGuid(), device.DeviceId, ports.Now.AddMinutes(-1), PingOutcome.Success, 40, 30));
        ports.Pings.Add(new(Guid.NewGuid(), device.DeviceId, ports.Now.AddMinutes(-2), PingOutcome.Timeout, null, null));
        await using var host = await ReadApiTestHost.StartAsync(ports);
        var path = $"/api/v1/devices/{device.DeviceId}/telemetry";
        using (var json = await GetAsync(host, path))
        {
            Fields(json.RootElement, "items", "page", "pageSize", "totalCount", "asOfUtc");
            var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(2, items.Length);
            Fields(items[0], "id", "deviceId", "observedAtUtc", "outcome", "latencyMs", "cpuPercent");
            Assert.Equal("Success", items[0].GetProperty("outcome").GetString());
            Assert.Equal("Timeout", items[1].GetProperty("outcome").GetString());
            Assert.Equal(JsonValueKind.Null, items[1].GetProperty("latencyMs").ValueKind);
            Utc(items[0].GetProperty("observedAtUtc"), ports.Now.AddMinutes(-1)); Utc(json.RootElement.GetProperty("asOfUtc"), ports.Now);
            Assert.Equal(ports.Now.AddDays(-1), ports.LastRange!.FromUtc); Assert.Equal(ports.Now, ports.LastRange.ToUtc);
        }
        using (await GetAsync(host, path + "?fromUtc=" + Stamp(ports.Now.AddHours(-2), z: false)))
            Assert.Equal(ports.Now.AddHours(-2), ports.LastRange!.FromUtc);
        using (await GetAsync(host, path + "?toUtc=" + Stamp(ports.Now.AddHours(-1))))
            Assert.Equal(ports.Now.AddHours(-25), ports.LastRange!.FromUtc);
        using (await GetAsync(host, path + "?fromUtc=" + Stamp(ports.Now.AddDays(-7)) + "&toUtc=" + Stamp(ports.Now)))
            Assert.Equal(TimeSpan.FromDays(7), ports.LastRange!.ToUtc - ports.LastRange.FromUtc);
        Assert.Equal(4, ports.ClockReads);
        int catalogBefore = ports.CatalogCalls;
        foreach (var query in new[]
        {
            "?fromUtc=" + Stamp(ports.Now) + "&toUtc=" + Stamp(ports.Now),
            "?fromUtc=" + Stamp(ports.Now.AddDays(-7).AddTicks(-1)) + "&toUtc=" + Stamp(ports.Now),
            "?toUtc=0001-01-01T01:00:00Z"
        }) await ProblemAsync(host, path + query, HttpStatusCode.BadRequest);
        Assert.Equal(catalogBefore, ports.CatalogCalls);
    }

    [Fact]
    public async Task Incidents_AllowlistedStateAndPagingWithoutDateFilters()
    {
        var ports = new FakeReadPorts(); var device = ports.AddDevice(1, retired: true);
        ports.Incidents.Add(new(Guid.NewGuid(), device.DeviceId, IncidentKind.Cpu, ports.Now.AddDays(-10), null, "CPU threshold exceeded"));
        ports.Incidents.Add(new(Guid.NewGuid(), device.DeviceId, IncidentKind.Latency, ports.Now.AddDays(-11), ports.Now.AddDays(-9), "Latency recovered"));
        await using var host = await ReadApiTestHost.StartAsync(ports);
        var path = $"/api/v1/devices/{device.DeviceId}/incidents";
        using var all = await GetAsync(host, path);
        Assert.Equal(2, all.RootElement.GetProperty("totalCount").GetInt64());
        var item = all.RootElement.GetProperty("items")[0]; Fields(item, "id", "deviceId", "kind", "openedAtUtc", "resolvedAtUtc", "summary");
        Assert.Equal("Cpu", item.GetProperty("kind").GetString()); Assert.Equal(JsonValueKind.Null, item.GetProperty("resolvedAtUtc").ValueKind);
        Utc(item.GetProperty("openedAtUtc"), ports.Now.AddDays(-10)); Utc(all.RootElement.GetProperty("asOfUtc"), ports.Now);
        using var open = await GetAsync(host, path + "?state=open&pageSize=1");
        Assert.Equal(1, open.RootElement.GetProperty("totalCount").GetInt64());
        using var resolved = await GetAsync(host, path + "?state=resolved&page=2&pageSize=1");
        Assert.Empty(resolved.RootElement.GetProperty("items").EnumerateArray()); Assert.Equal(1, resolved.RootElement.GetProperty("totalCount").GetInt64());
        await ProblemAsync(host, path + "?state=Open", HttpStatusCode.BadRequest);
        await ProblemAsync(host, path + "?fromUtc=" + Stamp(ports.Now.AddDays(-7)), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Metrics_RemainsGlobalAndPreservesFractionalFreshMean()
    {
        var ports = new FakeReadPorts();
        for (int i = 1; i <= 5; i++) ports.AddDevice(i);
        ports.AddDevice(6, retired: true);
        ports.States.Add(new(ports.Devices[0].DeviceId, ports.Now, ports.Now, PingOutcome.Success, 40, 30));
        ports.States.Add(new(ports.Devices[1].DeviceId, ports.Now, ports.Now, PingOutcome.Success, 41, 30));
        ports.States.Add(new(ports.Devices[2].DeviceId, ports.Now.AddSeconds(-91), ports.Now.AddSeconds(-91), PingOutcome.Success, 900, 90));
        ports.States.Add(new(ports.Devices[3].DeviceId, ports.Now, null, PingOutcome.Timeout, null, null));
        await using var host = await ReadApiTestHost.StartAsync(ports);
        using var grid = await GetAsync(host, "/api/v1/devices?q=device%2001&pageSize=1");
        Assert.Equal(1, grid.RootElement.GetProperty("totalCount").GetInt64());
        using var json = await GetAsync(host, "/api/v1/metrics/summary");
        var root = json.RootElement; Fields(root, "total", "healthy", "degraded", "offline", "unknown", "averageLatencyMs", "sampleCount", "asOfUtc");
        Assert.Equal(5, root.GetProperty("total").GetInt32()); Assert.Equal(2, root.GetProperty("healthy").GetInt32());
        Assert.Equal(0, root.GetProperty("degraded").GetInt32()); Assert.Equal(2, root.GetProperty("offline").GetInt32());
        Assert.Equal(1, root.GetProperty("unknown").GetInt32()); Assert.Equal(40.5m, root.GetProperty("averageLatencyMs").GetDecimal());
        Assert.Equal(2, root.GetProperty("sampleCount").GetInt32()); Utc(root.GetProperty("asOfUtc"), ports.Now);
    }

    [Fact]
    public async Task Failures_ReturnSanitizedProblemsAndSafeLogs()
    {
        var ports = new FakeReadPorts { ProviderSpecificBroadRule = true }; var device = ports.AddDevice(1);
        await using var host = await ReadApiTestHost.StartAsync(ports);
        foreach (var failure in new Exception[] { new InvalidOperationException(Canary), new ArgumentException(Canary),
            new OperationCanceledException(Canary), new Npgsql.NpgsqlException(Canary),
            new Npgsql.PostgresException(Canary, "ERROR", "ERROR", Npgsql.PostgresErrorCodes.UndefinedTable),
            new Npgsql.PostgresException(Canary, "ERROR", "ERROR", Npgsql.PostgresErrorCodes.InvalidTextRepresentation) })
        {
            ports.Failure = failure;
            await ProblemAsync(host, "/api/v1/devices", HttpStatusCode.InternalServerError);
            await ProblemAsync(host, $"/api/v1/devices/{device.DeviceId}/telemetry", HttpStatusCode.InternalServerError);
        }
        foreach (var outage in new Npgsql.NpgsqlException[]
        {
            new(Canary, new IOException(Canary)),
            new(Canary, new TimeoutException(Canary)),
            new Npgsql.PostgresException(Canary, "FATAL", "FATAL", Npgsql.PostgresErrorCodes.AdminShutdown)
        })
        {
            ports.Failure = outage;
            await ProblemAsync(host, "/api/v1/devices", HttpStatusCode.ServiceUnavailable);
        }
        // Use the installed provider strategy itself: fake ports alone do not reproduce EF's transient-error wrapper.
        // The delegate fails before any query or connection open; these options supply provider services only.
        await using var context = new DeviceDbContext(new DbContextOptionsBuilder<DeviceDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=healthmonitor_test").Options);
        var transport = new Npgsql.NpgsqlException(Canary, new IOException(Canary));
        var wrapped = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Database.CreateExecutionStrategy().ExecuteAsync(() => Task.FromException<int>(transport)));
        Assert.Same(transport, wrapped.InnerException);
        ports.Failure = wrapped;
        await ProblemAsync(host, "/api/v1/devices", HttpStatusCode.ServiceUnavailable);
        await ProblemAsync(host, $"/api/v1/devices/{device.DeviceId}/telemetry", HttpStatusCode.ServiceUnavailable);
        foreach (var failure in new Exception[]
        {
            new InvalidOperationException(Canary, new Npgsql.PostgresException(Canary, "ERROR", "ERROR", Npgsql.PostgresErrorCodes.UndefinedTable)),
            new InvalidOperationException(Canary, new Npgsql.PostgresException(Canary, "ERROR", "ERROR", Npgsql.PostgresErrorCodes.InvalidTextRepresentation)),
            new InvalidOperationException(Canary, new Npgsql.PostgresException(Canary, "ERROR", "ERROR", Npgsql.PostgresErrorCodes.SerializationFailure)),
            new InvalidOperationException(Canary, new Npgsql.NpgsqlException(Canary)),
            new Exception(Canary, transport),
            new InvalidOperationException(Canary, new InvalidOperationException(Canary, transport))
        })
        {
            ports.Failure = failure;
            await ProblemAsync(host, "/api/v1/devices", HttpStatusCode.InternalServerError);
        }
        var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
        loggerFactory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command").LogError(Canary);
        loggerFactory.CreateLogger("Npgsql.Command").LogError(Canary);
        loggerFactory.CreateLogger("Microsoft.Extensions.Diagnostics.HealthChecks.DefaultHealthCheckService").LogError(Canary);
        loggerFactory.CreateLogger("HealthMonitor.ContractProbe").LogWarning("Owned operational marker");
        ports.Failure = new DeviceCatalogCapacityExceededException();
        await ProblemAsync(host, "/api/v1/metrics/summary", HttpStatusCode.ServiceUnavailable);
        ports.Failure = null; ports.Now = default;
        await ProblemAsync(host, $"/api/v1/devices/{device.DeviceId}/telemetry", HttpStatusCode.InternalServerError);
        Assert.Contains(host.Logs.Entries, entry => entry.Contains("Read request failed.", StringComparison.Ordinal) &&
            entry.Contains("ErrorType=", StringComparison.Ordinal) && entry.Contains("TraceId=", StringComparison.Ordinal));
        Assert.Contains(host.Logs.Entries, entry => entry.Contains("Owned operational marker", StringComparison.Ordinal));
        Assert.All(host.Logs.Entries, entry => Assert.DoesNotContain(Canary, entry));
    }

    /// <summary>Uses explicit fake ports and two configured origins so the real CORS middleware must vary cached responses by origin.</summary>
    /// <returns>A caller-owned feature host; no runtime configuration, database, or external service is used.</returns>
    private static Task<ReadApiTestHost> CorsHostAsync() => ReadApiTestHost.StartAsync(services =>
    {
        var ports = new FakeReadPorts();
        services.AddSingleton<IDeviceCatalogQueries>(ports);
        services.AddSingleton<HealthMonitor.Application.Telemetry.IDeviceHealthQueries>(ports);
        services.AddSingleton<HealthMonitor.Application.Telemetry.IDeviceHistoryQueries>(ports);
        services.AddSingleton<TimeProvider>(ports);
        services.AddReadCors(new ConfigurationBuilder().AddInMemoryCollection(new[]
        {
            new KeyValuePair<string, string?>("Cors:AllowedOrigins:0", "http://localhost:5173"),
            new KeyValuePair<string, string?>("Cors:AllowedOrigins:1", "https://spa.example")
        }).Build());
    });

    /// <summary>Tests actual wire permissions; a successful preflight status alone does not grant the requested browser operation.</summary>
    /// <param name="response">Middleware response whose permission headers are inspected.</param>
    /// <param name="header">Permission header to inspect, treating comma-separated values individually.</param>
    /// <param name="value">Requested permission token or serialized origin.</param>
    /// <returns>True when the wire header grants the requested value.</returns>
    private static bool Allows(HttpResponseMessage response, string header, string value) =>
        response.Headers.TryGetValues(header, out var values)
        && values.SelectMany(entry => entry.Split(',')).Any(entry => string.Equals(entry.Trim(), value, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task Cors_AllowedOriginsReceiveExactReadHeaders()
    {
        await using var host = await CorsHostAsync();
        foreach (var origin in new[] { "http://localhost:5173", "https://spa.example" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/devices");
            request.Headers.Add("Origin", origin);
            using var response = await host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
            Assert.True(Allows(response, "Vary", "Origin"));
            Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
            // JSON Content-Type is already CORS-safelisted; no extra exposed-header configuration is needed.
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.False(response.Headers.Contains("Access-Control-Expose-Headers"));
        }
    }

    [Fact]
    public async Task Cors_UntrustedAndMissingOriginsDoNotGainBrowserAccess()
    {
        await using var host = await CorsHostAsync();
        foreach (var origin in new string?[] { null, "null", "https://untrusted.example", "http://localhost:5174", "https://spa.example.attacker.invalid", "http://spa.example" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/devices");
            if (origin is not null) request.Headers.Add("Origin", origin);
            using var response = await host.Client.SendAsync(request);
            // CORS is deliberately not authorization: direct callers and disallowed origins still reach public GET routes.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
            Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        }
    }

    [Fact]
    public async Task Cors_PreflightGrantsOnlyConfiguredReadMethodAndHeader()
    {
        await using var host = await CorsHostAsync();
        foreach (var (method, header, allowed) in new[]
        {
            ("GET", "Content-Type", true), ("POST", "Content-Type", false),
            ("PUT", "Content-Type", false), ("GET", "X-Demo-Operator-Key", false)
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/devices");
            request.Headers.Add("Origin", "https://spa.example");
            request.Headers.Add("Access-Control-Request-Method", method);
            request.Headers.Add("Access-Control-Request-Headers", header);
            using var response = await host.Client.SendAsync(request);
            bool permission = Allows(response, "Access-Control-Allow-Origin", "https://spa.example")
                && Allows(response, "Access-Control-Allow-Methods", method)
                && Allows(response, "Access-Control-Allow-Headers", header);
            Assert.Equal(allowed, permission);
            Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
            if (allowed)
            {
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                Assert.Equal("GET", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Methods")));
                Assert.Equal("Content-Type", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Headers")));
                Assert.True(Allows(response, "Vary", "Origin"));
            }
        }
    }
    /// <summary>Overrides only the built-in check registration; a pool resolution would fail, proving local probe HTTP cases are database-free.</summary>
    /// <param name="factory">Controlled check or registration-factory failure.</param>
    /// <param name="serviceFailure">Whether construction of the framework service fails before a check can run.</param>
    /// <param name="missingRegistration">Whether the required database check is absent, producing an empty filtered report.</param>
    /// <returns>Feature host with the production probe routes, framework timeout, and conflicting broad logging rule.</returns>
    private static async Task<ReadApiTestHost> ProbeHostAsync(Func<IServiceProvider, IHealthCheck> factory, bool serviceFailure = false, bool missingRegistration = false)
    {
        bool armed = false;
        var host = await ReadApiTestHost.StartAsync(services =>
        {
            services.AddSingleton<Npgsql.NpgsqlDataSource>(_ => throw new InvalidOperationException(Canary));
            if (serviceFailure)
            {
                // The publisher host also resolves this service during startup. Arm a test-only transient
                // factory failure afterward so the assertion specifically exercises the endpoint's guarded resolution.
                var originalService = services.Last(item => item.ServiceType == typeof(HealthCheckService));
                services.AddTransient<HealthCheckService>(provider => armed
                    ? throw new InvalidOperationException(Canary)
                    : (HealthCheckService)(originalService.ImplementationFactory?.Invoke(provider)
                        ?? ActivatorUtilities.CreateInstance(provider, originalService.ImplementationType!)));
            }
            services.PostConfigure<HealthCheckServiceOptions>(options =>
            {
                var original = options.Registrations.Single(check => check.Name == HealthProbeComposition.DatabaseCheckName);
                options.Registrations.Remove(original);
                if (!missingRegistration)
                    options.Registrations.Add(new HealthCheckRegistration(original.Name, factory, original.FailureStatus, original.Tags, original.Timeout));
            });
        }, providerSpecificBroadRule: true);
        armed = true;
        return host;
    }

    /// <summary>Adapts controlled async behavior to the standard health-check interface; no project-specific probe port is introduced.</summary>
    /// <param name="run">Healthy, failing, or cancellation-aware fake check behavior.</param>
    private sealed class ControlledHealthCheck(Func<CancellationToken, Task<HealthCheckResult>> run) : IHealthCheck
    {
        /// <inheritdoc />
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) => run(cancellationToken);
    }

    [Fact]
    public async Task Probes_LivenessDoesNotResolveDatabaseCheckOrPool()
    {
        int factories = 0;
        await using var host = await ProbeHostAsync(_ => { factories++; throw new InvalidOperationException(Canary); });
        using var response = await host.Client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Fields(json.RootElement, "status"); Assert.Equal("alive", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, factories);
        Assert.Empty(host.Services.GetServices<IHealthCheckPublisher>());
        Assert.All(host.Logs.Entries, entry => Assert.DoesNotContain(Canary, entry));
    }

    [Fact]
    public async Task Probes_ReadinessMapsResultsFactoryFailureTimeoutAndCancellationSafely()
    {
        foreach (var mode in new[] { "healthy", "unhealthy", "checkFailure", "factoryFailure", "serviceFailure", "missingRegistration", "timeout" })
        {
            bool timeoutCanceled = false;
            await using var host = await ProbeHostAsync(_ => mode == "factoryFailure"
                ? throw new InvalidOperationException(Canary)
                : new ControlledHealthCheck(async token =>
                {
                    if (mode == "timeout")
                    {
                        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                        catch (OperationCanceledException) { timeoutCanceled = true; throw; }
                    }
                    if (mode == "checkFailure") throw new InvalidOperationException(Canary);
                    return mode == "healthy" ? HealthCheckResult.Healthy()
                        : HealthCheckResult.Unhealthy(Canary, new InvalidOperationException(Canary));
                }), serviceFailure: mode == "serviceFailure", missingRegistration: mode == "missingRegistration");
            var registrations = host.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
            if (mode == "missingRegistration") Assert.Empty(registrations);
            else Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(registrations).Timeout);
            using var response = await host.Client.GetAsync("/health/ready");
            Assert.True(response.Headers.CacheControl?.NoStore);
            var text = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain(Canary, text);
            using var json = JsonDocument.Parse(text);
            if (mode == "healthy")
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode); Fields(json.RootElement, "status");
                Assert.Equal("ready", json.RootElement.GetProperty("status").GetString());
            }
            else
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
                Assert.Equal("database_unavailable", json.RootElement.GetProperty("code").GetString());
                Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
                Assert.Contains(host.Logs.Entries, entry => entry.Contains("Readiness unavailable.", StringComparison.Ordinal));
            }
            if (mode == "timeout") Assert.True(timeoutCanceled);
            Assert.All(host.Logs.Entries, entry => Assert.DoesNotContain(Canary, entry));
        }

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cancellationHost = await ProbeHostAsync(_ => new ControlledHealthCheck(async token =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
            return HealthCheckResult.Healthy();
        }));
        using var abort = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = cancellationHost.Client.GetAsync("/health/ready", abort.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(cancellationHost.Logs.Entries, entry => Assert.DoesNotContain(Canary, entry));
    }
    [Fact]
    public async Task RequestCancellation_ReachesPortsWithoutInternalError()
    {
        var ports = new FakeReadPorts(); ports.AddDevice(1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ports.OnRead = async token =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
        };
        await using var host = await ReadApiTestHost.StartAsync(ports);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = host.Client.GetAsync("/api/v1/devices", cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(host.Logs.Entries, entry => entry.Contains("Read request failed.", StringComparison.Ordinal));
        }
        finally
        {
            cancellation.Cancel();
            try { using var response = await request.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { /* Expected client cancellation; establish request completion before host disposal. */ }
        }
    }
}
