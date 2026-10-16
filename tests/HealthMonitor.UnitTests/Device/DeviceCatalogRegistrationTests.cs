using HealthMonitor.Api.Composition;
using HealthMonitor.Application.Device;
using HealthMonitor.Application.Telemetry;
using HealthMonitor.Infrastructure.Data.DeviceContext;
using Microsoft.Extensions.DependencyInjection;

namespace HealthMonitor.UnitTests.Device;

/// <summary>Checks the production registration helper without starting the host or configuring a database.</summary>
public sealed class DeviceCatalogRegistrationTests
{
    [Fact]
    public void AddDeviceCatalog_RegistersOnlyScopedQueriesAndAdapter()
    {
        var services = new ServiceCollection();
        Assert.Same(services, services.AddDeviceCatalog());
        Assert.Collection(services,
            descriptor =>
            {
                Assert.Equal(typeof(IDeviceCatalogQueries), descriptor.ServiceType);
                Assert.Equal(typeof(DeviceCatalogQueries), descriptor.ImplementationType);
                Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
            },
            descriptor =>
            {
                Assert.Equal(typeof(IDeviceCatalog), descriptor.ServiceType);
                Assert.Equal(typeof(DeviceCatalogAdapter), descriptor.ImplementationType);
                Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
            });
    }

    [Fact]
    public void AddDeviceCatalog_UsesExistingContextRegistrationAndResolvesPerScope()
    {
        var services = new ServiceCollection();
        services.AddDbContext<DeviceDbContext>(); // No provider/connection: resolution must not access storage.
        services.AddDeviceCatalog();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var queries = first.ServiceProvider.GetRequiredService<IDeviceCatalogQueries>();
        var catalog = first.ServiceProvider.GetRequiredService<IDeviceCatalog>();
        Assert.Same(queries, first.ServiceProvider.GetRequiredService<IDeviceCatalogQueries>());
        Assert.Same(catalog, first.ServiceProvider.GetRequiredService<IDeviceCatalog>());
        Assert.NotSame(queries, second.ServiceProvider.GetRequiredService<IDeviceCatalogQueries>());
        Assert.NotSame(catalog, second.ServiceProvider.GetRequiredService<IDeviceCatalog>());
        Assert.Single(services, d => d.ServiceType == typeof(DeviceDbContext));
    }
}
