using HealthMonitor.Infrastructure.Data.DeviceContext;
using HealthMonitor.Infrastructure.Data.TelemetryContext;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HealthMonitor.Api.Composition;

/// <summary>Validates database settings without connecting and owns the shared runtime pool registration.</summary>
/// <remarks>
/// Only ConnectionStrings:HealthMonitorRuntime supplies runtime credentials; maintenance
/// and integration-test settings are separate security boundaries, never fallback values.
/// Validation checks configuration without connecting. It cannot establish certificate trust,
/// target identity, effective grants, inherited privileges, or runtime DDL denial.
/// </remarks>
internal static class DatabaseConfiguration
{
    // Edition 1.5 budgets ten runtime connections per API process, separately from maintenance.
    internal const int RuntimeMaximumPoolSize = 10;

    /// <summary>Validates the canonical runtime setting and normalizes the approved pool budget.</summary>
    /// <param name="configuration">Application configuration; legacy and maintenance keys are never fallbacks.</param>
    /// <param name="environmentName">The environment already resolved by WebApplication.</param>
    /// <returns>Validated Npgsql configuration. This sensitive value must never be logged.</returns>
    /// <exception cref="InvalidOperationException">Required settings or security policy are invalid.</exception>
    internal static string GetRuntimeConnectionString(IConfiguration configuration, string environmentName)
    {
        const string key = "ConnectionStrings:HealthMonitorRuntime";
        var settings = ParseConnectionString(configuration.GetConnectionString("HealthMonitorRuntime"), key);
        // ContainsKey reports supported Npgsql keywords, including omitted defaults; ShouldSerialize checks explicit input.
        if (settings.ShouldSerialize("Maximum Pool Size") && settings.MaxPoolSize != RuntimeMaximumPoolSize)
            throw new InvalidOperationException($"{key} must use Maximum Pool Size={RuntimeMaximumPoolSize}.");
        if (settings.MinPoolSize > RuntimeMaximumPoolSize)
            throw new InvalidOperationException($"{key} must use Minimum Pool Size no greater than {RuntimeMaximumPoolSize}.");
        settings.MaxPoolSize = RuntimeMaximumPoolSize;

        if (settings.Port == 6543)
            throw new InvalidOperationException($"{key} cannot use transaction-pooler port 6543; use direct or session access.");

        if (!string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase)
            && settings.SslMode != SslMode.VerifyFull)
            throw new InvalidOperationException($"{key} requires SSL Mode=VerifyFull outside Development; configure trusted certificates.");

        return settings.ConnectionString;
    }

    /// <summary>Registers one DI-owned data source and two scoped, independently mapped contexts.</summary>
    /// <param name="services">The API composition container.</param>
    /// <param name="configuration">Application configuration validated immediately, before host construction.</param>
    /// <param name="environmentName">The resolved application environment.</param>
    /// <exception cref="InvalidOperationException">Runtime configuration violates the required settings or security policy.</exception>
    /// <remarks>
    /// One singleton shares the ten-connection budget across both contexts rather than creating
    /// a pool per module. DI disposes the source; scoped contexts borrow it and remain independently
    /// mapped to their own schemas and migration histories. Sharing a pool does not merge ownership
    /// or make a DbContext safe for parallel use. The budget is per API process, not per deployment.
    /// Building a data source does not open a connection. Migration credentials are not registered here.
    /// Actual least-privilege grants and hosted TLS trust require separate operational verification.
    /// </remarks>
    internal static void AddRuntimeDatabase(IServiceCollection services, IConfiguration configuration, string environmentName)
    {
        string connectionString = GetRuntimeConnectionString(configuration, environmentName);
        services.AddSingleton<NpgsqlDataSource>(_ => new NpgsqlDataSourceBuilder(connectionString).Build());
        services.AddDbContext<DeviceDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>(), pg =>
            {
                pg.MigrationsAssembly("HealthMonitor.Infrastructure");
                pg.MigrationsHistoryTable("__ef_migrations_history", "device");
            }));
        services.AddDbContext<TelemetryDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>(), pg =>
            {
                pg.MigrationsAssembly("HealthMonitor.Infrastructure");
                pg.MigrationsHistoryTable("__ef_migrations_history", "telemetry");
            }));
    }

    /// <summary>Parses the supported explicit-host/database/user mode while suppressing provider diagnostic values.</summary>
    /// <param name="connectionString">Sensitive configuration, never included in exceptions.</param>
    /// <param name="key">A fixed configuration key used in actionable diagnostics.</param>
    /// <returns>Parsed settings without opening a connection.</returns>
    /// <exception cref="InvalidOperationException">The setting is absent, malformed, or incomplete.</exception>
    internal static NpgsqlConnectionStringBuilder ParseConnectionString(string? connectionString, string key)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"{key} must be configured.");

        NpgsqlConnectionStringBuilder settings;
        try
        {
            settings = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            // Provider exceptions can contain input values; do not retain an inner exception.
            throw new InvalidOperationException($"{key} must contain valid Npgsql connection-string settings.");
        }

        if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.Database)
            || string.IsNullOrWhiteSpace(settings.Username))
            throw new InvalidOperationException($"{key} must specify Host, Database, and Username.");
        return settings;
    }
}
