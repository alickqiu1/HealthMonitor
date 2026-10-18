using HealthMonitor.Api.Fleet;
using HealthMonitor.Application.Telemetry;
using HealthMonitor.Domain.Telemetry;
using HealthMonitor.Infrastructure.Data.TelemetryContext;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HealthMonitor.Api.Composition;

/// <summary>Registers fleet reads and validates fixed Edition 1.5 Monitoring rules without database access.</summary>
internal static class FleetReadServiceCollectionExtensions
{
    /// <summary>Checks fixed monitoring values and adds scoped latest-state and fleet readers.</summary>
    /// <param name="services">Services with Device queries and TelemetryDbContext registered separately.</param>
    /// <param name="configuration">Nonsecret settings; absent values use canonical Domain constants.</param>
    /// <returns>The same collection.</returns>
    /// <exception cref="ArgumentNullException">A dependency is null.</exception>
    /// <exception cref="InvalidOperationException">A configured value is malformed or differs from its fixed rule.</exception>
    /// <remarks>
    /// Monitoring:OfflineAfterSeconds=90, LatencyWarningMs=250, and CpuWarningPercent=80 document fixed rules,
    /// not adjustable policies. Unsupported overrides fail so read health and incident decisions cannot diverge.
    /// An existing clock is preserved. Registration and validation do not open a database connection.
    /// </remarks>
    internal static IServiceCollection AddFleetReads(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        RequireFixed(configuration, "Monitoring:OfflineAfterSeconds", TelemetryThresholds.OfflineAfterSeconds);
        RequireFixed(configuration, "Monitoring:LatencyWarningMs", TelemetryThresholds.LatencyWarningMs);
        RequireFixed(configuration, "Monitoring:CpuWarningPercent", TelemetryThresholds.CpuWarningPercent);
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddScoped<IDeviceHealthQueries, DeviceHealthQueries>();
        services.AddScoped<FleetReadService>();
        return services;
    }

    /// <summary>Checks one nonsecret fixed value without echoing arbitrary configuration into diagnostics.</summary>
    /// <param name="configuration">Host configuration.</param>
    /// <param name="key">Owned Monitoring key.</param>
    /// <param name="expected">Canonical Domain value.</param>
    /// <exception cref="InvalidOperationException">The supplied setting is not the supported numeric value.</exception>
    private static void RequireFixed(IConfiguration configuration, string key, decimal expected)
    {
        var supplied = configuration[key];
        if (supplied is not null && (!decimal.TryParse(supplied, System.Globalization.NumberStyles.AllowLeadingSign |
            System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowLeadingWhite |
            System.Globalization.NumberStyles.AllowTrailingWhite, System.Globalization.CultureInfo.InvariantCulture,
            out var actual) || actual != expected))
            throw new InvalidOperationException($"{key} must match the fixed Edition 1.5 value {expected}.");
    }
}
