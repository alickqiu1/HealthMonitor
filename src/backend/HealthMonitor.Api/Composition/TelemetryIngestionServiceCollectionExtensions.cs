using HealthMonitor.Application.Telemetry;
using HealthMonitor.Domain.Telemetry;
using HealthMonitor.Infrastructure.Data.TelemetryContext;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HealthMonitor.Api.Composition;

/// <summary>Wires ingestion without changing database selection, pool ownership, or context registration.</summary>
internal static class TelemetryIngestionServiceCollectionExtensions
{
    /// <summary>Registers the Application workflow and atomic adapter against the same scoped Telemetry context.</summary>
    /// <param name="services">Host services; TelemetryDbContext and the Device catalog port must be registered separately.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="ArgumentNullException">The service collection is null.</exception>
    /// <remarks>One scope is one write attempt. A failed adapter disposes its context; retry in a fresh scope with the same BatchId.</remarks>
    internal static IServiceCollection AddTelemetryIngestion(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddScoped<IncidentLifecycleProcessor>();
        services.AddScoped<ISimulationBatchRepository, SimulationBatchRepository>();
        services.AddScoped<ITelemetryIngestionTransaction, TelemetryIngestionTransaction>();
        services.AddScoped<ITelemetryIngestionService, TelemetryIngestionService>();
        return services;
    }
}
