using HealthMonitor.Infrastructure.Data.TelemetryContext;
using Microsoft.EntityFrameworkCore;

namespace HealthMonitor.UnitTests.Telemetry;

/// <summary>Proves latest-state query validation/empty paths do not open a database connection.</summary>
public sealed class DeviceHealthQueriesTests
{
    /// <summary>Creates a configured context with an unreachable host; every test must return before opening it.</summary>
    private static TelemetryDbContext Context() => new(new DbContextOptionsBuilder<TelemetryDbContext>()
        .UseNpgsql("Host=example.invalid;Database=fictional;Username=unused").Options);

    [Fact]
    public void Constructor_NullContext_Throws() => Assert.Throws<ArgumentNullException>(() => new DeviceHealthQueries(null!));

    [Fact]
    public async Task NullIds_ThrowsWithoutConnection()
    {
        await using var context = Context();
        await Assert.ThrowsAsync<ArgumentNullException>(() => new DeviceHealthQueries(context).GetLatestByDeviceIdsAsync(null!));
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreCancelled_IncludingEmptyInput_Propagates(bool empty)
    {
        await using var context = Context();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new DeviceHealthQueries(context)
            .GetLatestByDeviceIdsAsync(empty ? Array.Empty<Guid>() : new[] { Guid.NewGuid() }, cancellation.Token));
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task EmptyInput_ReturnsNoRowsWithoutConnection()
    {
        await using var context = Context();
        Assert.Empty(await new DeviceHealthQueries(context).GetLatestByDeviceIdsAsync(Array.Empty<Guid>()));
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task MoreThanThousandDistinctIds_RejectsWithoutConnection()
    {
        await using var context = Context();
        var ids = Enumerable.Range(0, 1001).Select(_ => Guid.NewGuid()).ToArray();
        await Assert.ThrowsAsync<ArgumentException>(() => new DeviceHealthQueries(context).GetLatestByDeviceIdsAsync(ids));
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task DuplicateEmptyIds_CollapseAndReturnNoRowsWithoutConnection()
    {
        await using var context = Context();
        Assert.Empty(await new DeviceHealthQueries(context).GetLatestByDeviceIdsAsync(Enumerable.Repeat(Guid.Empty, 1001).ToArray()));
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }
}
