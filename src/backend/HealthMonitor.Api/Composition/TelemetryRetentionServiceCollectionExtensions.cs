using HealthMonitor.Api.Telemetry;
using HealthMonitor.Application.Telemetry;
using HealthMonitor.Infrastructure.Data.TelemetryContext;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HealthMonitor.Api.Composition;

/// <summary>Registers retention independently of DemoProducer enablement or public HTTP routes.</summary>
internal static class TelemetryRetentionServiceCollectionExtensions
{
    /// <summary>Adds the Application coordinator, SQL store, and startup/hourly worker without opening a connection.</summary>
    /// <param name="services">Host services with a separately configured scoped TelemetryDbContext.</param>
    /// <returns>The same collection.</returns>
    /// <exception cref="ArgumentNullException">The service collection is null.</exception>
    /// <remarks>The store and coordinator share the worker's fresh scope; the existing runtime data source and grants remain authoritative.</remarks>
    internal static IServiceCollection AddTelemetryRetention(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddScoped<ITelemetryRetentionStore, TelemetryRetentionStore>();
        services.AddScoped<TelemetryRetentionService>();
        services.AddHostedService<TelemetryRetentionWorker>();
        return services;
    }
}
