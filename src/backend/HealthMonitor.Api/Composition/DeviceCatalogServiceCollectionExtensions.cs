using HealthMonitor.Application.Device;
using HealthMonitor.Application.Telemetry;
using HealthMonitor.Infrastructure.Data.DeviceContext;

namespace HealthMonitor.Api.Composition;

/// <summary>Registers the catalog boundary without changing Device persistence configuration.</summary>
internal static class DeviceCatalogServiceCollectionExtensions
{
    /// <summary>Adds scoped query and adapter services that share the configured Device context.</summary>
    /// <param name="services">The host's non-null service collection; DeviceDbContext must be registered separately.</param>
    /// <returns>The same collection for composition.</returns>
    /// <exception cref="ArgumentNullException">The service collection is null.</exception>
    /// <remarks>
    /// The API owns the bridge so Telemetry depends only on its application port, not Device
    /// persistence. Scoped lifetimes align with DeviceDbContext; callers must not invoke queries
    /// concurrently within a scope or retain the adapter after that scope is disposed.
    /// </remarks>
    internal static IServiceCollection AddDeviceCatalog(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IDeviceCatalogQueries, DeviceCatalogQueries>();
        services.AddScoped<IDeviceCatalog, DeviceCatalogAdapter>();
        return services;
    }
}
