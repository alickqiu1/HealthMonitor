using HealthMonitor.Infrastructure.Data.DeviceContext;
using Microsoft.EntityFrameworkCore;

namespace HealthMonitor.UnitTests.Device;

/// <summary>Verifies catalog guards and the empty-request path without database configuration or access.</summary>
public sealed class DeviceCatalogQueriesTests
{
    /// <summary>Omits the provider deliberately so guards and empty paths fail if they attempt database work.</summary>
    private static DeviceDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DeviceDbContext>().Options);

    [Fact]
    public void Constructor_NullContext_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new DeviceCatalogQueries(null!));

    [Fact]
    public async Task GetByIdsAsync_Empty_ReturnsEmptyWithoutProvider()
    {
        await using var context = CreateContext();
        var result = await new DeviceCatalogQueries(context).GetByIdsAsync(Array.Empty<Guid>());
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetByIdsAsync_Null_Throws()
    {
        await using var context = CreateContext();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            new DeviceCatalogQueries(context).GetByIdsAsync(null!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetByIdsAsync_Canceled_ThrowsBeforeProviderAccess(bool empty)
    {
        await using var context = CreateContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DeviceCatalogQueries(context).GetByIdsAsync(
                empty ? Array.Empty<Guid>() : new[] { Guid.NewGuid() }, cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task GetActiveAsync_Canceled_ThrowsBeforeProviderAccess()
    {
        await using var context = CreateContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DeviceCatalogQueries(context).GetActiveAsync(cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task GetByIdsAsync_UnconfiguredProvider_FailureIsNotReturnedAsEmpty()
    {
        await using var context = CreateContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DeviceCatalogQueries(context).GetByIdsAsync(new[] { Guid.NewGuid() }));
    }

    [Fact]
    public async Task GetActiveAsync_UnconfiguredProvider_FailureIsNotReturnedAsEmpty()
    {
        await using var context = CreateContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DeviceCatalogQueries(context).GetActiveAsync());
    }
    [Fact]
    public async Task GetDetailAsync_Empty_ReturnsNullWithoutProvider()
    {
        await using var context = CreateContext();
        Assert.Null(await new DeviceCatalogQueries(context).GetDetailAsync(Guid.Empty));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetDetailAsync_PreCanceled_IncludingEmpty_RejectsBeforeProvider(bool empty)
    {
        await using var context = CreateContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DeviceCatalogQueries(context).GetDetailAsync(empty ? Guid.Empty : Guid.NewGuid(), cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    [Fact]
    public async Task GetDetailAsync_ProviderFailure_DoesNotMasqueradeAsUnknown()
    {
        await using var context = CreateContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DeviceCatalogQueries(context).GetDetailAsync(Guid.NewGuid()));
    }
}
