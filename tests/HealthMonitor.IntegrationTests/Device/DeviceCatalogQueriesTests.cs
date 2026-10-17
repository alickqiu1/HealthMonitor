using System.Data;
using HealthMonitor.Application.Device;
using HealthMonitor.Application.IntegrationContracts;
using HealthMonitor.Domain.Device;
using HealthMonitor.Infrastructure.Data.DeviceContext;
using HealthMonitor.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HealthMonitor.IntegrationTests.Device;

/// <summary>Exercises real PostgreSQL catalog projections inside rollback-only test transactions.</summary>
/// <remarks>
/// Requires both Device migrations and explicitly authorized, exclusive use of the test database.
/// The existing fixture must authorize and verify healthmonitor_test before context creation.
/// No migrations, commits, deletes, schema operations, or changes to existing rows occur here.
/// Repeatable-read snapshots isolate each scenario from unrelated concurrent catalog inserts.
/// Context and transaction disposal roll back only this test's new rows even on assertion failure.
/// </remarks>
[Collection(DeviceDatabaseCollection.Name)]
public sealed class DeviceCatalogQueriesTests
{
    /// <summary>Uses the fixture-verified connection without transferring its ownership to EF.</summary>
    /// <param name="connection">Open connection owned and disposed by the test.</param>
    /// <returns>A caller-owned context; each writing scenario supplies its rollback-only transaction.</returns>
    private static DeviceDbContext CreateContext(NpgsqlConnection connection) =>
        new(new DbContextOptionsBuilder<DeviceDbContext>().UseNpgsql(connection).Options);

    /// <summary>Creates an unpersisted canonical registration with unique identity and tag.</summary>
    /// <param name="retired">Whether to apply a deterministic retirement timestamp.</param>
    /// <returns>A domain entity to insert only inside the test-owned transaction.</returns>
    private static MonitoredDevice CreateDevice(bool retired = false)
    {
        var device = new MonitoredDevice(Guid.NewGuid(), $"QUERY-{Guid.NewGuid():N}",
            "Catalog query test", "Server", "DEN", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        if (retired)
            device.Retire(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero));
        return device;
    }

    /// <summary>Builds the expected application projection independently of the production SQL projection.</summary>
    /// <param name="device">Known baseline or test-owned entity, including its retirement state.</param>
    /// <returns>The six inventory values expected from a catalog query.</returns>
    private static DeviceRegistration Registration(MonitoredDevice device) =>
        new(device.Id, device.DisplayName, device.AssetTag, device.SiteCode,
            device.DeviceType, device.RetiredAtUtc.HasValue);

    [Fact]
    public async Task GetByIdsAsync_MixedDuplicatesUnknownAndRetired_ProjectsDistinctOrderedUntrackedRecords()
    {
        await using var connection = await new PostgreSqlTestFixture().OpenConnectionAsync();
        await using var context = CreateContext(connection);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var active = CreateDevice();
        var retired = CreateDevice(retired: true);
        context.AddRange(active, retired);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        Guid missing;
        do { missing = Guid.NewGuid(); } while (await context.Devices.AnyAsync(d => d.Id == missing));
        var queries = new DeviceCatalogQueries(context);
        var ids = new[] { retired.Id, missing, active.Id, retired.Id, active.Id };
        var expected = new[] { Registration(active), Registration(retired) }.OrderBy(d => d.DeviceId).ToArray();

        var first = await queries.GetByIdsAsync(ids);
        var second = await queries.GetByIdsAsync(ids.Reverse().ToArray());
        Assert.Equal(expected, first.ToArray());
        Assert.Equal(expected, second.ToArray());
        Assert.Empty(await queries.GetByIdsAsync(new[] { missing }));
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task GetActiveAsync_ExcludesRetiredAndPreservesCompleteOrderedProjection()
    {
        await using var connection = await new PostgreSqlTestFixture().OpenConnectionAsync();
        await using var context = CreateContext(connection);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var existing = await context.Devices.AsNoTracking().Where(d => d.RetiredAtUtc == null).ToArrayAsync();
        Assert.True(existing.Length < IDeviceCatalogQueries.MaximumActiveDevices,
            "This scenario requires room for one test-owned active row; do not remove unrelated data.");
        var active = CreateDevice();
        var retired = CreateDevice(retired: true);
        context.AddRange(active, retired);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var expected = existing.Select(Registration).Append(Registration(active)).OrderBy(d => d.DeviceId).ToArray();

        var result = await new DeviceCatalogQueries(context).GetActiveAsync();
        Assert.Equal(expected, result.ToArray());
        Assert.DoesNotContain(result, d => d.IsRetired || d.DeviceId == retired.Id);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1001)]
    public async Task GetActiveAsync_ExactCapacityAndOverflow_AreNotSilentlyTruncated(int targetCount)
    {
        await using var connection = await new PostgreSqlTestFixture().OpenConnectionAsync();
        await using var context = CreateContext(connection);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        int existing = await context.Devices.CountAsync(d => d.RetiredAtUtc == null);
        Assert.True(existing <= 1000, "Boundary setup cannot remove unrelated rows from an already over-capacity catalog.");
        context.AddRange(Enumerable.Range(0, targetCount - existing).Select(_ => CreateDevice()));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var queries = new DeviceCatalogQueries(context);

        if (targetCount == 1001)
            await Assert.ThrowsAsync<DeviceCatalogCapacityExceededException>(() => queries.GetActiveAsync());
        else
            Assert.Equal(1000, (await queries.GetActiveAsync()).Count);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Queries_PreCanceledToken_PropagatesCancellation()
    {
        await using var connection = await new PostgreSqlTestFixture().OpenConnectionAsync();
        await using var context = CreateContext(connection);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var queries = new DeviceCatalogQueries(context);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            queries.GetByIdsAsync(new[] { Guid.NewGuid() }, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queries.GetActiveAsync(cancellation.Token));
    }
}
