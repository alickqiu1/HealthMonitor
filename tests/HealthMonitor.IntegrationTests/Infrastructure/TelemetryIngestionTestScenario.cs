using HealthMonitor.Application.IntegrationContracts;
using HealthMonitor.Application.Telemetry;
using HealthMonitor.Domain.Telemetry;
using HealthMonitor.Infrastructure.Data.TelemetryContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace HealthMonitor.IntegrationTests.Infrastructure;

/// <summary>Serializes ingestion and lock tests that share the global advisory lock.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryIngestionDatabaseCollection
{
    /// <summary>The single-process collection; the disposable database must also be exclusively owned.</summary>
    public const string Name = "Telemetry ingestion database";
}

/// <summary>Owns only proven-absent test device identities and batch receipts in the validated disposable database.</summary>
/// <remarks>
/// No Device rows, schema changes, or broad deletes are needed: the catalog double supplies
/// eligibility facts while these tests exercise real Telemetry persistence. Every writer is
/// registered and drained before cleanup. Unresponsive work stops the host without unsafe cleanup.
/// A failed run requires discarding the disposable database, never adopting unknown leftovers.
/// </remarks>
internal sealed class TelemetryIngestionTestScenario : TimeProvider
{
    private readonly NpgsqlDataSource _source;
    private readonly List<Guid> _batches = new();
    private readonly List<Task> _writers = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(60));
    // Millisecond precision round-trips through PostgreSQL and matches UUIDv7 time precision.
    internal DateTimeOffset UtcNow { get; set; } = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    internal Guid[] DeviceIds { get; } = new[] { Guid.NewGuid(), Guid.NewGuid() };
    internal CancellationToken Token => _timeout.Token;
    public override DateTimeOffset GetUtcNow() => UtcNow;
    private TelemetryIngestionTestScenario(NpgsqlDataSource source) => _source = source;

    /// <summary>Supplies catalog facts without coupling Telemetry transaction tests to Device writes.</summary>
    internal sealed class Catalog : IDeviceCatalog
    {
        internal bool Retired { get; set; }
        internal bool Missing { get; set; }
        public Task<IReadOnlyList<DeviceRegistration>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<DeviceRegistration>>(Missing ? Array.Empty<DeviceRegistration>()
                : ids.Select(id => new DeviceRegistration(id, "Integration test", "TEST", "DEN", "Server", Retired)).ToArray());
        }
    }

    /// <summary>Constructs a caller-owned context on the fixture-validated source, optionally with test-only interceptors.</summary>
    internal TelemetryDbContext Context(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<TelemetryDbContext>().UseNpgsql(_source).AddInterceptors(interceptors).Options);

    /// <summary>Injects cleanup failure through EF's test-only transaction factory replacement.</summary>
    internal TelemetryDbContext CleanupFailureContext(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<TelemetryDbContext>().UseNpgsql(_source)
            .ReplaceService<Microsoft.EntityFrameworkCore.Storage.IRelationalTransactionFactory,
                HealthMonitor.IntegrationTests.Telemetry.TelemetryIngestionTransactionTests.CleanupFailureTransactionFactory>()
            .AddInterceptors(interceptors).Options);
    /// <summary>Builds a real service/adapter/repository chain sharing exactly the supplied context.</summary>
    internal TelemetryIngestionService Service(TelemetryDbContext context, Catalog? catalog = null) =>
        new(new SimulationBatchRepository(context), catalog ?? new Catalog(),
            new TelemetryIngestionTransaction(context, new SimulationBatchRepository(context),
                new IncidentLifecycleProcessor(), this, NullLogger<TelemetryIngestionTransaction>.Instance), this);

    /// <summary>Reserves a proven-absent request identity before any possible write or uncertain commit.</summary>
    internal async Task<TelemetryIngestionRequest> RequestAsync(string scenario = "Healthy")
    {
        Guid batchId = Guid.CreateVersion7(UtcNow);
        await using var context = Context();
        Assert.False(await context.SimulationBatches.AnyAsync(batch => batch.BatchId == batchId, Token));
        _batches.Add(batchId);
        return new(batchId, scenario, Array.AsReadOnly(DeviceIds.ToArray()));
    }

    /// <summary>Registers writing work before returning its task so exceptional body exits cannot race cleanup.</summary>
    internal Task<TelemetryIngestionResult> Start(TelemetryIngestionService service, TelemetryIngestionRequest request,
        CancellationToken token = default)
    {
        var writer = service.IngestAsync(request, token == default ? Token : token);
        _writers.Add(writer);
        return writer;
    }

    internal ValueTask<NpgsqlConnection> OpenConnectionAsync() => _source.OpenConnectionAsync(Token);

    /// <summary>Runs a bounded scenario and cleans only reserved identities after every registered writer terminates.</summary>
    /// <remarks>Provider cancellation is cooperative; independent deadlines stop the host if setup/body/writers cannot finish.</remarks>
    internal static async Task RunAsync(Func<TelemetryIngestionTestScenario, Task> body)
    {
        using var setupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var setup = new PostgreSqlTestFixture().CreateDataSourceAsync(setupTimeout.Token);
        await StopIfUnresponsiveAsync(setup, TimeSpan.FromSeconds(45));
        await using var source = await setup;
        var scenario = new TelemetryIngestionTestScenario(source);
        bool ownsIds = false;
        Exception? failure = null;
        try
        {
            // A UUID collision is not permission to adopt existing state or incident history.
            await StopIfUnresponsiveAsync(scenario.VerifyAbsentIdsAsync(), TimeSpan.FromSeconds(45));
            ownsIds = true;
            await StopIfUnresponsiveAsync(body(scenario), TimeSpan.FromSeconds(90));
        }
        catch (Exception exception) { failure = exception; }
        try { scenario._timeout.Cancel(); }
        catch (Exception cancellation) { failure = failure is null ? cancellation : new AggregateException(failure, cancellation); }
        try { await StopIfUnresponsiveAsync(Task.WhenAll(scenario._writers), TimeSpan.FromSeconds(30)); }
        catch { /* The body asserts expected writer failures; this barrier only establishes termination. */ }
        if (ownsIds)
        {
            try { await scenario.CleanupAsync(); }
            catch (Exception cleanup) { failure = failure is null ? cleanup : new AggregateException(failure, cleanup); }
        }
        scenario._timeout.Dispose();
        if (failure is not null) global::System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Checks all Telemetry references before reserving generated test identities.</summary>
    private async Task VerifyAbsentIdsAsync()
    {
        await using var context = Context();
        Assert.False(await context.TelemetryPings.AnyAsync(ping => DeviceIds.Contains(ping.DeviceId), Token));
        Assert.False(await context.DeviceTelemetryStates.AnyAsync(state => DeviceIds.Contains(state.DeviceId), Token));
        Assert.False(await context.IncidentLogs.AnyAsync(incident => DeviceIds.Contains(incident.DeviceId), Token));
    }

    /// <summary>Deletes reserved rows in FK-safe order with a fresh cleanup token, never broadening ownership.</summary>
    private async Task CleanupAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = await _source.OpenConnectionAsync(timeout.Token);
        await using var command = new NpgsqlCommand("""
            DELETE FROM telemetry.device_telemetry_state WHERE device_id = ANY(@devices);
            DELETE FROM telemetry.incident_logs WHERE device_id = ANY(@devices);
            DELETE FROM telemetry.telemetry_pings WHERE batch_id = ANY(@batches);
            DELETE FROM telemetry.simulation_batches WHERE batch_id = ANY(@batches);
            """, connection);
        command.Parameters.AddWithValue("devices", DeviceIds);
        command.Parameters.AddWithValue("batches", _batches.ToArray());
        await command.ExecuteNonQueryAsync(timeout.Token);
    }

    /// <summary>Stops without cleanup if asynchronous work does not finish; a timeout alone cannot make live writers safe.</summary>
    private static async Task StopIfUnresponsiveAsync(Task work, TimeSpan deadline)
    {
        using var timer = new CancellationTokenSource();
        if (await Task.WhenAny(work, Task.Delay(deadline, timer.Token)) != work)
        {
            Console.Error.WriteLine("Ingestion test exceeded its deadline; discard the disposable database. Stopping without cleanup.");
            Environment.Exit(1);
        }
        timer.Cancel();
        await work;
    }
}
