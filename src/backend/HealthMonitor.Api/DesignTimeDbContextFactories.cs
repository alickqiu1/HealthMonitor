
using HealthMonitor.Infrastructure.Data.DeviceContext;
using HealthMonitor.Infrastructure.Data.TelemetryContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace HealthMonitor.Api;

/// <summary>
/// Supplies migration credentials to EF Core tooling.
/// Normal API requests do not use these factories.
/// </summary>
internal static class DesignTimeDatabaseConfiguration
{
    public static string GetMigrationConnectionString()
    {
        // Read User Secrets from the API startup assembly.
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<DeviceDbContextFactory>(
                optional: false)
            .Build();

        string? connectionString =
            configuration.GetConnectionString("HealthMonitor");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:HealthMonitor " +
                "must be configured for EF Core migrations.");
        }

        return connectionString;
    }
}

/// <summary>
/// Design-time factory for Device migrations.
/// </summary>
public sealed class DeviceDbContextFactory
    : IDesignTimeDbContextFactory<DeviceDbContext>
{
    public DeviceDbContext CreateDbContext(string[] args)
    {
        string connectionString =
            DesignTimeDatabaseConfiguration
                .GetMigrationConnectionString();

        var options = new DbContextOptionsBuilder<DeviceDbContext>();

        options.UseNpgsql(
            connectionString,
            pg =>
            {
                pg.MigrationsAssembly(
                    "HealthMonitor.Infrastructure");

                pg.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "device");
            });

        return new DeviceDbContext(options.Options);
    }
}

/// <summary>
/// Design-time factory for Telemetry migrations.
/// </summary>
public sealed class TelemetryDbContextFactory
    : IDesignTimeDbContextFactory<TelemetryDbContext>
{
    public TelemetryDbContext CreateDbContext(string[] args)
    {
        string connectionString =
            DesignTimeDatabaseConfiguration
                .GetMigrationConnectionString();

        var options = new DbContextOptionsBuilder<TelemetryDbContext>();

        options.UseNpgsql(
            connectionString,
            pg =>
            {
                pg.MigrationsAssembly(
                    "HealthMonitor.Infrastructure");

                pg.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "telemetry");
            });

        return new TelemetryDbContext(options.Options);
    }
}
