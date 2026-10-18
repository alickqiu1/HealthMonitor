using System.Data;
using System.Data.Common;
using System.Runtime.ExceptionServices;
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

/// <summary>Owns real HTTP/query proof only on fixture-verified healthmonitor_test, preserving the Device baseline by never committing its inserts.</summary>
/// <remarks>
/// Device request contexts share an externally owned connection/rollback transaction and are used sequentially only.
/// Telemetry contexts use independent leases from the same verified source and retain their own history transactions.
/// No full Program, workers, concurrent shared-connection proof, or hosted operations are involved.
/// </remarks>
internal sealed class ReadApiDatabaseScenario
{
    private readonly TelemetryIngestionTestScenario telemetry;
    internal ReadApiTestHost Host { get; private set; } = null!;
    internal CommandProbe Probe { get; } = new();
    internal MonitoredDevice[] Devices { get; }
    internal int BaselineActive { get; }
    internal DateTimeOffset Now => telemetry.UtcNow;
    internal string Marker { get; }
    internal Guid UnknownId { get; private set; }
    private readonly List<Task> requests = new();

    /// <summary>Captures only owned facts after absence/capacity checks, before any feature request.</summary>
    private ReadApiDatabaseScenario(TelemetryIngestionTestScenario telemetry, MonitoredDevice[] devices, int baselineActive, string marker)
    {
        this.telemetry = telemetry; Devices = devices; BaselineActive = baselineActive; Marker = marker;
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
    /// <returns>Completion after baseline preservation checks.</returns>
    internal static Task RunAsync(Func<ReadApiDatabaseScenario, Task> body, bool retireSecond = false) =>
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
            var scenario = new ReadApiDatabaseScenario(test, devices, active, marker);
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
                        .UseLoggerFactory(provider.GetRequiredService<ILoggerFactory>()).AddInterceptors(scenario.Probe));
                    services.AddDeviceCatalog();
                    services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(test);
                });
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
