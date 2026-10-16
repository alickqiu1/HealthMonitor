using System.Runtime.ExceptionServices;
using HealthMonitor.Api.Provisioning;
using HealthMonitor.Domain.Device;
using HealthMonitor.Infrastructure.Data.DeviceContext;
using HealthMonitor.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HealthMonitor.IntegrationTests.Provisioning;

/// <summary>Owns provisioning test records across the provisioner's independently committed transactions.</summary>
/// <remarks>
/// Requires the serialized Device collection and exclusive use of the test database by this
/// test process. Fixed seed identities must be absent before ownership is reserved. Never
/// adopt or delete pre-existing seed records. No database DDL or broad deletion is performed.
/// </remarks>
internal sealed class DeviceProvisioningTestScenario
{
    private readonly HashSet<Guid> _ownedIds = new();
    private readonly List<Task> _writers = new();
    private readonly NpgsqlDataSource _source;
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(45));

    private DeviceProvisioningTestScenario(NpgsqlDataSource source) => _source = source;

    /// <summary>Complete immutable row snapshot, including timestamps, for preservation assertions.</summary>
    internal sealed record Row(Guid Id, string AssetTag, string DisplayName, string DeviceType,
        string SiteCode, DateTimeOffset CreatedAtUtc, DateTimeOffset? RetiredAtUtc);

    /// <summary>The baseline belongs to others and must be unchanged after test-owned cleanup.</summary>
    internal Row[] Baseline { get; private set; } = Array.Empty<Row>();

    /// <summary>Bounds provisioner calls and scenario database work, including lock waits.</summary>
    internal CancellationToken Token => _timeout.Token;

    /// <summary>Creates the unchanged production provisioner with a validated test data source.</summary>
    internal DeviceCatalogProvisioner CreateProvisioner() => new(_source);

    /// <summary>Registers a concurrent writer before its task escapes to the test body.</summary>
    /// <param name="cancellationToken">Cancellation requested by the concurrency test.</param>
    /// <returns>The production operation; scenario teardown also observes it before cleanup.</returns>
    internal Task<int> StartProvisioningAsync(CancellationToken cancellationToken)
    {
        Task<int> writer = CreateProvisioner().ProvisionAsync(cancellationToken);
        _writers.Add(writer);
        return writer;
    }

    /// <summary>Opens a caller-owned verified-source connection for the concurrency barrier.</summary>
    internal ValueTask<NpgsqlConnection> OpenConnectionAsync() => _source.OpenConnectionAsync(Token);

    /// <summary>Runs initialized scenarios, drains writers, and preserves setup/body/cleanup failures.</summary>
    /// <param name="action">Body with a 90-second outer deadline; concurrent writers must use StartProvisioningAsync.</param>
    /// <returns>Completion after all writers terminate and owned records are cleaned up.</returns>
    /// <remarks>
    /// An unresponsive operation terminates the test host instead of returning to teardown:
    /// a timeout exception alone would let cleanup race with an unresolved writer. The CI
    /// hang watchdog is a second boundary. Such failure requires discarding the disposable
    /// database; never automatically recover unknown leftovers in a shared local database.
    /// </remarks>
    internal static async Task RunAsync(Func<DeviceProvisioningTestScenario, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var setupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        NpgsqlDataSource? source = null;
        DeviceProvisioningTestScenario? scenario = null;
        bool initialized = false;
        Exception? failure = null;
        try
        {
            Task<NpgsqlDataSource> setup = new PostgreSqlTestFixture().CreateDataSourceAsync(setupTimeout.Token);
            await AwaitOrStopHostAsync(setup, TimeSpan.FromSeconds(45), "data-source initialization");
            source = await setup;
            scenario = new DeviceProvisioningTestScenario(source);
            Task<Row[]> baseline = scenario.SnapshotAsync();
            await AwaitOrStopHostAsync(baseline, TimeSpan.FromSeconds(60), "baseline capture");
            scenario.Baseline = await baseline;
            var ids = FixedDeviceCatalog.Devices.Select(d => d.Id).ToHashSet();
            var tags = FixedDeviceCatalog.Devices.Select(d => d.AssetTag).ToHashSet(StringComparer.Ordinal);
            Assert.DoesNotContain(scenario.Baseline, row => ids.Contains(row.Id) || tags.Contains(row.AssetTag));
            // No write or cleanup ownership is authorized until the complete baseline is known.
            scenario._ownedIds.UnionWith(ids);
            initialized = true;
            await AwaitOrStopHostAsync(action(scenario), TimeSpan.FromSeconds(90), "scenario body");
        }
        catch (Exception exception) { failure = exception; }

        if (scenario is not null && initialized)
        {
            try { scenario._timeout.Cancel(); }
            catch (Exception cancellation)
            {
                failure = Combine(failure, cancellation, "Scenario and cancellation both failed.");
            }
            // Covers exceptional exits before the test body reached its own WhenAll. Never
            // delete rows, dispose the data source, or advance to another test with live writers.
            Task writers = Task.WhenAll(scenario._writers);
            try { await AwaitOrStopHostAsync(writers, TimeSpan.FromSeconds(30), "writer shutdown"); }
            catch (Exception workers)
            {
                failure = Combine(failure, writers.Exception ?? workers, "Scenario and writer operations failed.");
            }
            try { await scenario.CleanupAsync(); }
            catch (Exception cleanup)
            {
                failure = Combine(failure, cleanup, "Test and owned-record cleanup both failed.");
            }
        }
        // Incomplete setup owns no writes: skip database cleanup and baseline comparison.
        scenario?._timeout.Dispose();
        if (source is not null)
        {
            try { await source.DisposeAsync(); }
            catch (Exception disposal)
            {
                failure = Combine(failure, disposal, "Scenario and resource disposal both failed.");
            }
        }
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Retains the primary failure when a later teardown operation also fails.</summary>
    private static Exception Combine(Exception? primary, Exception secondary, string message) =>
        primary is null ? secondary : new AggregateException(message, primary, secondary);

    /// <summary>Bounds asynchronous work without allowing a timeout to release unsafe cleanup.</summary>
    /// <param name="operation">Operation that must reach a terminal state before execution continues.</param>
    /// <param name="deadline">Grace-inclusive outer bound, independent of provider cancellation cooperation.</param>
    /// <param name="phase">Nonsecret phase label for the failed test-host diagnostic.</param>
    private static async Task AwaitOrStopHostAsync(Task operation, TimeSpan deadline, string phase)
    {
        using var timer = new CancellationTokenSource();
        Task expired = Task.Delay(deadline, timer.Token);
        if (await Task.WhenAny(operation, expired) != operation)
        {
            Console.Error.WriteLine($"Provisioning test {phase} exceeded its outer deadline; terminating the test host without cleanup to avoid unresolved database writers.");
            // Exit the test host with failure, without unwinding finally/teardown code. The
            // runner aborts this group and CI discards its isolated PostgreSQL service.
            Environment.Exit(1);
        }
        timer.Cancel();
        await operation;
    }
    /// <summary>Reads fresh committed row state in stable identifier order.</summary>
    internal async Task<Row[]> SnapshotAsync()
    {
        await using var connection = await _source.OpenConnectionAsync(Token);
        return await ReadRowsAsync(connection, Token);
    }

    /// <summary>Builds a deterministic seed-shaped test row; it has not yet been persisted.</summary>
    internal static Row SeedRow(DeviceSeedDefinition seed) => new(seed.Id, seed.AssetTag,
        seed.DisplayName, seed.DeviceType, seed.SiteCode,
        new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), null);

    /// <summary>Builds an unrelated row with a random identity and unique inventory tag.</summary>
    internal static Row ExtraRow(bool retired = false)
    {
        var row = new Row(Guid.NewGuid(), $"PROVISION-TEST-{Guid.NewGuid():N}", "Provisioning test",
            "Server", "DEN", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), null);
        return retired ? row with { RetiredAtUtc = row.CreatedAtUtc.AddDays(1) } : row;
    }

    /// <summary>Reserves absent setup identities before an atomic insert, including uncertain commit outcomes.</summary>
    /// <param name="rows">Unique, previously absent test-owned rows; baseline records must never be supplied.</param>
    /// <returns>Completion after the setup insert commits, within the scenario deadline.</returns>
    internal async Task InsertAsync(params Row[] rows)
    {
        if (rows.Length == 0) return;
        await using var context = new DeviceDbContext(
            new DbContextOptionsBuilder<DeviceDbContext>().UseNpgsql(_source).Options);
        foreach (var row in rows)
        {
            var device = new MonitoredDevice(row.Id, row.AssetTag, row.DisplayName, row.DeviceType,
                row.SiteCode, row.CreatedAtUtc);
            if (row.RetiredAtUtc.HasValue) device.Retire(row.RetiredAtUtc.Value);
            context.Add(device);
        }
        Guid[] ids = rows.Select(row => row.Id).Distinct().ToArray();
        string[] tags = rows.Select(row => row.AssetTag).Distinct(StringComparer.Ordinal).ToArray();
        Assert.Equal(rows.Length, ids.Length);
        Assert.Equal(rows.Length, tags.Length);
        Assert.False(await context.Devices.AnyAsync(device => ids.Contains(device.Id) || tags.Contains(device.AssetTag), Token),
            "Setup cannot adopt an existing record or inventory tag.");
        // Reserve only proven-absent identities before writing. If cancellation loses a commit
        // response, cleanup still knows which possible inserts belong exclusively to this test.
        _ownedIds.UnionWith(ids);
        await context.SaveChangesAsync(Token);
    }

    /// <summary>Deletes only owned identities, then verifies all baseline data survived unchanged.</summary>
    private async Task CleanupAsync()
    {
        // A canceled body must not suppress cleanup; never retry by broadening the deletion.
        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = await _source.OpenConnectionAsync(cleanupTimeout.Token);
        if (_ownedIds.Count != 0)
        {
            await using var command = new NpgsqlCommand(
                "DELETE FROM device.monitored_devices WHERE id = ANY(@ids)", connection);
            command.Parameters.AddWithValue("ids", _ownedIds.ToArray());
            await command.ExecuteNonQueryAsync(cleanupTimeout.Token);
        }
        Assert.Equal(Baseline, await ReadRowsAsync(connection, cleanupTimeout.Token));
    }

    /// <summary>Reads only Device rows; does not query or mutate the Telemetry schema.</summary>
    private static async Task<Row[]> ReadRowsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT id, asset_tag, display_name, device_type, site_code, created_at_utc, retired_at_utc
            FROM device.monitored_devices ORDER BY id
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<Row>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new Row(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6)));
        return rows.ToArray();
    }
}
