using HealthMonitor.Api.Telemetry;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HealthMonitor.Api.Composition;

/// <summary>Registers the optional, server-owned producer without opening a database connection.</summary>
internal static class DemoProducerServiceCollectionExtensions
{
    /// <summary>Adds the producer only when explicitly enabled; local deterministic demos default to disabled.</summary>
    /// <param name="services">Host services, including the separately registered ingestion and catalog boundaries.</param>
    /// <param name="configuration">Configuration containing only producer enablement and cadence settings here.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="ArgumentNullException">A dependency is null.</exception>
    /// <exception cref="InvalidOperationException">An enabled producer requests a cadence other than 60 seconds.</exception>
    /// <remarks>Enable on one continuously running API replica only. This registration supplies no distributed leader election.</remarks>
    internal static IServiceCollection AddDemoProducer(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection("DemoProducer").Get<DemoProducerOptions>() ?? new();
        if (!options.Enabled)
            return services;
        if (options.IntervalSeconds != 60)
            throw new InvalidOperationException("DemoProducer:IntervalSeconds must be 60 when enabled.");

        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddHostedService<DemoTelemetryProducer>();
        return services;
    }
}

/// <summary>Nonsecret startup settings for the Edition 1.5 synthetic producer.</summary>
internal sealed class DemoProducerOptions
{
    /// <summary>Whether this single API replica generates telemetry; false preserves operator-controlled local scenarios.</summary>
    public bool Enabled { get; set; }

    /// <summary>The required 60-second cadence. Other enabled values require an architecture decision.</summary>
    public int IntervalSeconds { get; set; } = 60;
}
