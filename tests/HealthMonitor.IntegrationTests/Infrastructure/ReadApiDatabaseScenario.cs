using System.Data;
using System.Data.Common;
using System.Runtime.ExceptionServices;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using HealthMonitor.Api.Composition;
using HealthMonitor.Application.IntegrationContracts;
using HealthMonitor.Domain.Device;
using HealthMonitor.Domain.Telemetry;
using HealthMonitor.Infrastructure.Data.DeviceContext;
using HealthMonitor.Infrastructure.Data.TelemetryContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HealthMonitor.IntegrationTests.Infrastructure;

/// <summary>Owns real HTTP/query/ingestion proof only on fixture-verified healthmonitor_test, preserving the Device baseline by never committing its inserts.</summary>
/// <remarks>
/// Device request contexts share an externally owned connection/rollback transaction and are used sequentially only.
/// Telemetry contexts use independent leases from the same verified source and retain their own history transactions.
/// No full Program, application producer/retention workers, concurrent shared-connection proof, or hosted operations are involved.
/// </remarks>
internal sealed class ReadApiDatabaseScenario
{
    private readonly TelemetryIngestionTestScenario telemetry;
    // Borrow the fixture-owned parent context only for proven-owned inventory changes; never dispose it here.
    private readonly DeviceDbContext ownedDeviceContext;
    /// <summary>Configured before a request, never during one; applies only to subsequent scoped API Telemetry contexts.</summary>
    internal List<IInterceptor> TelemetryInterceptors { get; } = new();
    internal ReadApiTestHost Host { get; private set; } = null!;
    internal CommandProbe Probe { get; } = new();
    internal MonitoredDevice[] Devices { get; }
    internal int BaselineActive { get; }
    internal DateTimeOffset Now => telemetry.UtcNow;
    internal string Marker { get; }
    internal Guid UnknownId { get; private set; }
    private readonly List<Task> requests = new();
    private readonly HashSet<Guid> reservedHttpBatches = new();

    /// <summary>Captures only owned facts after absence/capacity checks, before any feature request.</summary>
    private ReadApiDatabaseScenario(TelemetryIngestionTestScenario telemetry, MonitoredDevice[] devices, int baselineActive, string marker, DeviceDbContext ownedDeviceContext)
    {
        this.telemetry = telemetry; Devices = devices; BaselineActive = baselineActive; Marker = marker; this.ownedDeviceContext = ownedDeviceContext;
    }

    /// <summary>Registers every client request as well as the host's independent server-side barriers.</summary>
    /// <param name="path">Relative V1 URL.</param>
    /// <returns>Caller-owned response after full body buffering.</returns>
    internal Task<HttpResponseMessage> GetAsync(string path)
    {
        var request = telemetry.TrackWriter(Host.Client.GetAsync(path, telemetry.Token));
        requests.Add(request); return request;
    }

    /// <summary>Runs one sequential fixture scenario; stops/drains the feature before rollback, then existing Telemetry ownership cleanup runs.</summary>
    /// <param name="body">Assertions against real controllers and both real query modules.</param>
    /// <param name="retireSecond">Whether explicit detail must read retired inventory.</param>
    /// <param name="enableSimulation">Whether to compose real ingestion behind fabricated local network facts; default remains disabled.</param>
    /// <returns>Completion after baseline preservation checks.</returns>
    internal static Task RunAsync(Func<ReadApiDatabaseScenario, Task> body, bool retireSecond = false, bool enableSimulation = false) =>
        TelemetryIngestionTestScenario.RunAsync(async test =>
        {
            await test.RequireEmptyTelemetryAsync();
            await using var source = await test.IndependentSourceAsync();
            await using var connection = await source.OpenConnectionAsync(test.Token);
            // UseNpgsql(DbConnection) does not transfer ownership; the scenario outlives every request context.
            await using var deviceContext = new DeviceDbContext(new DbContextOptionsBuilder<DeviceDbContext>().UseNpgsql(connection).Options);
            await using var transaction = await deviceContext.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, test.Token);
            var baseline = await InventoryAsync(deviceContext, test.Token);
            string marker = $"HTTP-{Guid.NewGuid():N}";
            var devices = new[]
            {
                new MonitoredDevice(test.DeviceIds[0], marker + "-A", marker + " Alpha", "Server", "DEN", test.UtcNow.AddDays(-3)),
                new MonitoredDevice(test.DeviceIds[1], marker + "-B", marker + " Beta", "Database", "DEN", test.UtcNow.AddDays(-3))
            };
            if (retireSecond) devices[1].Retire(test.UtcNow.AddDays(-1));
            Assert.DoesNotContain(baseline, item => devices.Any(d => d.Id == item.Registration.DeviceId || d.AssetTag == item.Registration.AssetTag));
            int active = baseline.Count(d => !d.Registration.IsRetired);
            Assert.True(active + devices.Count(d => d.RetiredAtUtc is null) <= 1000, "Do not remove unrelated inventory to make fixture capacity.");
            var scenario = new ReadApiDatabaseScenario(test, devices, active, marker, deviceContext);
            Guid unknown;
            do { unknown = Guid.NewGuid(); } while (baseline.Any(d => d.Registration.DeviceId == unknown) || devices.Any(d => d.Id == unknown));
            scenario.UnknownId = unknown;
            Exception? failure = null; bool rolledBack = false;
            try
            {
                deviceContext.Devices.AddRange(devices);
                await deviceContext.SaveChangesAsync(test.Token);
                deviceContext.ChangeTracker.Clear();
                await test.TrackWriter(scenario.SeedTelemetryAsync(retireSecond));
                scenario.Host = await ReadApiTestHost.StartAsync(services =>
                {
                    // Instance registration borrows the fixture-owned pool; the host must not become its disposal owner.
                    services.AddSingleton(source);
                    services.AddScoped(provider =>
                    {
                        var context = new DeviceDbContext(new DbContextOptionsBuilder<DeviceDbContext>()
                            .UseNpgsql(connection).UseLoggerFactory(provider.GetRequiredService<ILoggerFactory>())
                            .AddInterceptors(scenario.Probe).Options);
                        try { context.Database.UseTransaction(transaction.GetDbTransaction()); return context; }
                        catch
                        {
                            try { context.Dispose(); } catch { /* Preserve attachment failure; connection/transaction remain externally owned. */ }
                            throw;
                        }
                    });
                    services.AddDbContext<TelemetryDbContext>((provider, options) => options.UseNpgsql(source)
                        .UseLoggerFactory(provider.GetRequiredService<ILoggerFactory>()).AddInterceptors(scenario.Probe).AddInterceptors(scenario.TelemetryInterceptors));
                    services.AddDeviceCatalog();
                    services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(test);
                    if (enableSimulation)
                    {
                        services.AddTelemetryIngestion();
                        services.AddSingleton<IStartupFilter>(new SimulationTestHost.NetworkFilter(new SimulationTestHost.NetworkFacts()));
                    }
                }, simulationConfiguration: new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Simulation:HttpEndpointEnabled"] = enableSimulation.ToString(),
                    ["Simulation:AllowLocalAnonymous"] = enableSimulation.ToString(),
                    ["Cors:AllowedOrigins:0"] = "http://localhost:5173"
                }).Build(), environmentName: enableSimulation ? "Development" : "Testing");
                if (enableSimulation) SimulationTestHost.ApplyBindings(scenario.Host, "http://127.0.0.1:5000");
                await body(scenario);
            }
            catch (Exception error) { failure = error; }

            // A canceled client task is insufficient. Host disposal closes admission and drains actual server execution first.
            await TelemetryIngestionTestScenario.DrainWritersAsync(scenario.requests.ToArray());
            if (scenario.Host is not null)
                try { await scenario.Host.DisposeAsync(); }
                catch (Exception cleanup) { failure = Combine(failure, cleanup); }
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await transaction.RollbackAsync(cleanup.Token);
                rolledBack = true;
            }
            catch (Exception cleanup) { failure = Combine(failure, cleanup); }
            if (rolledBack)
            {
                try
                {
                    using var check = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    Assert.Equal(baseline, await InventoryAsync(deviceContext, check.Token));
                }
                catch (Exception check) { failure = Combine(failure, check); }
            }
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        });

    /// <summary>Reserves every receipt before HTTP can write or encounter an uncertain outcome.</summary>
    /// <returns>A proven-absent UUIDv7 at the controlled fixture clock.</returns>
    internal async Task<Guid> ReserveBatchAsync()
    {
        Guid batch = Guid.CreateVersion7(Now);
        await telemetry.ReserveBatchIdsAsync(new[] { batch });
        Assert.True(reservedHttpBatches.Add(batch));
        return batch;
    }

    /// <summary>Advances only the Application clock; cancellation deadlines and the actual HTTP quota retain real time.</summary>
    /// <param name="elapsed">Positive UTC advance, for observation ordering and replay-age tests.</param>
    internal void AdvanceClock(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        telemetry.UtcNow = telemetry.UtcNow.Add(elapsed);
    }

    /// <summary>Registers POST work before returning it so client completion cannot race with fixture cleanup.</summary>
    /// <param name="batch">Receipt identity previously reserved by this scenario.</param>
    /// <param name="scenario">Canonical synthetic scenario.</param>
    /// <param name="ids">Only fixture-owned selected identities.</param>
    /// <returns>Caller-owned buffered response; the host separately drains server pipelines.</returns>
    internal Task<HttpResponseMessage> PostAsync(Guid batch, string scenario, params Guid[] ids)
    {
        Assert.Contains(batch, reservedHttpBatches);
        Assert.All(ids, id => Assert.Contains(id, telemetry.DeviceIds));
        var request = telemetry.TrackWriter(Host.Client.PostAsJsonAsync(SimulationTestHost.Route,
            new { batchId = batch, deviceIds = ids, scenario }, telemetry.Token));
        requests.Add(request); return request;
    }

    /// <summary>Retires only a proven-owned registration inside the parent rollback-only Device transaction.</summary>
    /// <param name="index">Index into the fixture's two proven-owned device registrations.</param>
    /// <returns>Completion after saving only the owned retirement field.</returns>
    internal async Task RetireOwnedDeviceAsync(int index)
    {
        var device = await RequireOwnedDeviceAsync(index);
        ownedDeviceContext.Devices.Attach(device); device.Retire(Now);
        ownedDeviceContext.Entry(device).Property(row => row.RetiredAtUtc).IsModified = true;
        await ownedDeviceContext.SaveChangesAsync(telemetry.Token); ownedDeviceContext.ChangeTracker.Clear();
    }

    /// <summary>Removes only a proven-owned inserted row to exercise unknown-catalog rejection; rollback restores the original baseline.</summary>
    /// <param name="index">Index into the fixture's two proven-owned device registrations.</param>
    /// <returns>Completion after deleting exactly that inserted row inside the rollback-only transaction.</returns>
    internal async Task RemoveOwnedRegistrationAsync(int index)
    {
        var device = await RequireOwnedDeviceAsync(index);
        Assert.Equal(1, await ownedDeviceContext.Devices.Where(row => row.Id == device.Id).ExecuteDeleteAsync(telemetry.Token));
        ownedDeviceContext.ChangeTracker.Clear();
    }

    /// <summary>Checks both reserved identity and exact seeded tag before any test-owned catalog mutation.</summary>
    private async Task<MonitoredDevice> RequireOwnedDeviceAsync(int index)
    {
        if (index < 0 || index >= Devices.Length) throw new ArgumentOutOfRangeException(nameof(index));
        var owned = Devices[index]; Assert.Contains(owned.Id, telemetry.DeviceIds);
        var stored = await ownedDeviceContext.Devices.AsNoTracking().SingleAsync(row => row.Id == owned.Id, telemetry.Token);
        Assert.Equal(owned.AssetTag, stored.AssetTag);
        return owned;
    }

    /// <summary>Captures all mapped scalar facts in all four Telemetry tables for exact preservation assertions.</summary>
    /// <remarks>Requires an empty Telemetry baseline and the exclusively owned, single-process disposable-database contract. Callers await serial HTTP acknowledgments first; this is not a cross-process guard or production snapshot API.</remarks>
    /// <returns>Deterministically ordered persisted facts, excluding computed/nonmapped domain properties.</returns>
    internal async Task<string> TelemetrySnapshotAsync()
    {
        await using var context = telemetry.Context();
        var pings = await context.TelemetryPings.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(telemetry.Token);
        var states = await context.DeviceTelemetryStates.AsNoTracking().OrderBy(row => row.DeviceId).ToArrayAsync(telemetry.Token);
        var incidents = await context.IncidentLogs.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(telemetry.Token);
        var receipts = await context.SimulationBatches.AsNoTracking().OrderBy(row => row.BatchId).ToArrayAsync(telemetry.Token);
        return JsonSerializer.Serialize(new
        {
            pings = pings.Select(row => Facts(context, row)), states = states.Select(row => Facts(context, row)),
            incidents = incidents.Select(row => Facts(context, row)), receipts = receipts.Select(row => Facts(context, row))
        });
    }

    /// <summary>Uses EF mapped properties so preservation tests do not depend on future computed domain getters.</summary>
    private static Dictionary<string, object?> Facts(DbContext context, object entity) => context.Entry(entity).Properties
        .OrderBy(property => property.Metadata.Name, StringComparer.Ordinal).ToDictionary(property => property.Metadata.Name, property => property.CurrentValue);

    /// <summary>Preserves original assertion/setup failures while reporting cleanup failures too.</summary>
    private static Exception Combine(Exception? original, Exception cleanup) => original is null ? cleanup : new AggregateException(original, cleanup);

    /// <summary>Captures all baseline scalar lifecycle facts; rollback must preserve every unrelated row without deleting or updating it.</summary>
    private static Task<DeviceCatalogDetail[]> InventoryAsync(DeviceDbContext context, CancellationToken token) =>
        context.Devices.AsNoTracking().OrderBy(d => d.Id).Select(d => new DeviceCatalogDetail(
            new DeviceRegistration(d.Id, d.DisplayName, d.AssetTag, d.SiteCode, d.DeviceType, d.RetiredAtUtc != null),
            d.CreatedAtUtc, d.RetiredAtUtc)).ToArrayAsync(token);

    /// <summary>Seeds domain-valid query facts directly, not a claim about canonical scenario-generator output or ingestion replay.</summary>
    /// <param name="retired">Whether second-device history predates its retirement.</param>
    private async Task<bool> SeedTelemetryAsync(bool retired)
    {
        var batches = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        await telemetry.ReserveBatchIdsAsync(batches);
        var pings = new[]
        {
            TelemetryPing.CreateSuccess(Guid.NewGuid(), Devices[0].Id, batches[0], Now.AddHours(-2), 600, 95m),
            TelemetryPing.CreateTimeout(Guid.NewGuid(), Devices[0].Id, batches[1], Now.AddHours(-1)),
            TelemetryPing.CreateSuccess(Guid.NewGuid(), Devices[0].Id, batches[2], Now, 40, 95m),
            TelemetryPing.CreateSuccess(Guid.NewGuid(), Devices[1].Id, batches[3], retired ? Now.AddDays(-1).AddMinutes(-1) : Now, 41, 30m)
        };
        await using var context = telemetry.Context();
        foreach (var ping in pings)
        {
            context.SimulationBatches.Add(SimulationBatch.CreateCommitted(ping.BatchId, new string('a', 64),
                ping.Outcome == PingOutcome.Timeout ? "Timeout" : "Healthy", ping.ObservedAtUtc, ping.ObservedAtUtc, 1));
            context.TelemetryPings.Add(ping);
        }
        context.DeviceTelemetryStates.AddRange(DeviceTelemetryState.Create(pings[2]), DeviceTelemetryState.Create(pings[3]));
        context.IncidentLogs.Add(IncidentLog.Open(Guid.NewGuid(), Devices[0].Id, IncidentKind.Cpu, pings[0].ObservedAtUtc, pings[0].Id, "CPU exceeded threshold"));
        var resolved = IncidentLog.Open(Guid.NewGuid(), Devices[0].Id, IncidentKind.Latency, pings[0].ObservedAtUtc, pings[0].Id, "Latency exceeded threshold");
        resolved.Resolve(pings[2].ObservedAtUtc, pings[2].Id);
        context.IncidentLogs.Add(resolved);
        await context.SaveChangesAsync(telemetry.Token);
        return true;
    }

    /// <summary>Counts actual EF commands or injects a transport failure at their boundary; never simulates a real network outage.</summary>
    internal sealed class CommandProbe : DbCommandInterceptor
    {
        internal int Count;
        internal Exception? Failure;
        /// <inheritdoc />
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count);
            if (Failure is not null) throw Failure;
            return ValueTask.FromResult(result);
        }
        /// <inheritdoc />
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count);
            if (Failure is not null) throw Failure;
            return ValueTask.FromResult(result);
        }
    }
}
