using HealthMonitor.Api.Composition;
using HealthMonitor.Application.Device;
using HealthMonitor.Application.IntegrationContracts;

namespace HealthMonitor.UnitTests.Device;

/// <summary>Verifies the module bridge forwards inputs/results and does not reinterpret failures or retirement.</summary>
public sealed class DeviceCatalogAdapterTests
{
    /// <summary>Captures explicit lookup forwarding and rejects fleet-list requests outside Telemetry's port.</summary>
    private sealed class StubQueries : IDeviceCatalogQueries
    {
        public Func<IReadOnlyCollection<Guid>, CancellationToken, Task<IReadOnlyList<DeviceRegistration>>> Lookup { get; init; }
            = (_, _) => throw new InvalidOperationException("Unexpected lookup.");

        public Task<IReadOnlyList<DeviceRegistration>> GetByIdsAsync(
            IReadOnlyCollection<Guid> deviceIds, CancellationToken cancellationToken = default) =>
            Lookup(deviceIds, cancellationToken);

        public Task<IReadOnlyList<DeviceRegistration>> GetActiveAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Telemetry must not request fleet listing.");
    }

    [Fact]
    public void Constructor_NullQueries_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new DeviceCatalogAdapter(null!));

    [Fact]
    public async Task GetByIdsAsync_ForwardsIdsTokenAndExactResultIncludingRetired()
    {
        Guid[] ids = { Guid.NewGuid(), Guid.NewGuid() };
        IReadOnlyList<DeviceRegistration> expected = new[]
        {
            new DeviceRegistration(ids[0], "Active", "TEST-A", "DEN", "Server", false),
            new DeviceRegistration(ids[1], "Retired", "TEST-R", "DEN", "Gateway", true)
        };
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        var queries = new StubQueries
        {
            Lookup = (actualIds, token) =>
            {
                calls++;
                Assert.Same(ids, actualIds);
                Assert.Equal(cancellation.Token, token);
                return Task.FromResult(expected);
            }
        };

        var result = await new DeviceCatalogAdapter(queries).GetByIdsAsync(ids, cancellation.Token);
        Assert.Same(expected, result);
        Assert.Equal(1, calls);
        Assert.True(result[1].IsRetired);
    }

    [Fact]
    public async Task GetByIdsAsync_Empty_ForwardsWithoutInventingPolicy()
    {
        Guid[] ids = Array.Empty<Guid>();
        var queries = new StubQueries
        {
            Lookup = (actualIds, _) =>
            {
                Assert.Same(ids, actualIds);
                return Task.FromResult<IReadOnlyList<DeviceRegistration>>(Array.Empty<DeviceRegistration>());
            }
        };
        Assert.Empty(await new DeviceCatalogAdapter(queries).GetByIdsAsync(ids));
    }

    [Fact]
    public async Task GetByIdsAsync_DependencyFailure_PropagatesSameException()
    {
        var failure = new InvalidOperationException("Synthetic dependency failure.");
        var queries = new StubQueries
        {
            Lookup = (_, _) => Task.FromException<IReadOnlyList<DeviceRegistration>>(failure)
        };
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DeviceCatalogAdapter(queries).GetByIdsAsync(new[] { Guid.NewGuid() }));
        Assert.Same(failure, actual);
    }

    [Fact]
    public async Task GetByIdsAsync_Cancellation_PropagatesOriginalTaskAndToken()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Task<IReadOnlyList<DeviceRegistration>> canceled =
            Task.FromCanceled<IReadOnlyList<DeviceRegistration>>(cancellation.Token);
        var queries = new StubQueries { Lookup = (_, _) => canceled };
        var actualTask = new DeviceCatalogAdapter(queries).GetByIdsAsync(
            new[] { Guid.NewGuid() }, cancellation.Token);
        Assert.Same(canceled, actualTask);
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => actualTask);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }
}
