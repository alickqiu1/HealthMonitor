
using HealthMonitor.Infrastructure.Data.DeviceContext;
using HealthMonitor.Infrastructure.Data.TelemetryContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace HealthMonitor.Api;

/// <summary>
/// Provides the privileged PostgreSQL connection string required
/// by EF Core design-time migration operations.
/// </summary>
/// <remarks>
/// EF Core tooling uses this configuration when creating Device
/// or Telemetry DbContexts through their design-time factories.
///
/// Migration credentials are read from the API project's User Secrets
/// under ConnectionStrings:HealthMonitor.
///
/// This connection string is intended for schema management and
/// private database administration. Normal API requests instead
/// use the separately configured HealthMonitorRuntime connection.
///
/// Database access restrictions are enforced through PostgreSQL
/// permissions, not through this configuration helper.
///
/// Connection strings must never be committed to source control
/// or included in application logs.
/// </remarks>
internal static class DesignTimeDatabaseConfiguration
{
    /// <summary>
    /// Retrieves the configured privileged migration connection string.
    /// </summary>
    /// <returns>
    /// The non-empty PostgreSQL connection string used by EF Core migrations.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the migration connection string is missing or blank.
    /// </exception>
    /// <remarks>
    /// User Secrets are loaded using the API assembly's secret identifier.
    ///
    /// This method does not open a database connection or execute
    /// migrations. It supplies configuration to the design-time factories.
    /// </remarks>
    public static string GetMigrationConnectionString()
    {
        // Resolve the API project's User Secrets independently of
        // the normal runtime dependency-injection configuration.
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
/// Creates the Device DbContext for EF Core design-time tooling.
/// </summary>
/// <remarks>
/// This factory supports Device schema migrations without requiring
/// the API's normal runtime dependency-injection container.
///
/// The context uses privileged migration credentials and a dedicated
/// migration history table in the PostgreSQL "device" schema.
///
/// Normal API operations do not use this factory.
/// </remarks>
public sealed class DeviceDbContextFactory
    : IDesignTimeDbContextFactory<DeviceDbContext>
{
    /// <summary>
    /// Creates a configured Device DbContext for EF Core tooling.
    /// </summary>
    /// <param name="args">
    /// Design-time arguments supplied by EF Core tooling.
    /// This implementation does not currently consume them.
    /// </param>
    /// <returns>
    /// A Device DbContext configured with the PostgreSQL provider
    /// and Device-specific migration history.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the required migration connection string is missing.
    /// </exception>
    /// <remarks>
    /// The caller is responsible for disposing the returned context.
    /// This method configures the context but does not execute migrations.
    /// </remarks>
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
/// Creates the Telemetry DbContext for EF Core design-time tooling.
/// </summary>
/// <remarks>
/// This factory supports Telemetry schema migrations independently
/// of the Device DbContext and normal API runtime configuration.
///
/// The context uses privileged migration credentials and a dedicated
/// migration history table in the PostgreSQL "telemetry" schema.
///
/// Separate migration histories allow each bounded context to
/// manage its own database schema changes.
/// </remarks>
public sealed class TelemetryDbContextFactory
    : IDesignTimeDbContextFactory<TelemetryDbContext>
{
    /// <summary>
    /// Creates a configured Telemetry DbContext for EF Core tooling.
    /// </summary>
    /// <param name="args">
    /// Design-time arguments supplied by EF Core tooling.
    /// This implementation does not currently consume them.
    /// </param>
    /// <returns>
    /// A Telemetry DbContext configured with the PostgreSQL provider
    /// and Telemetry-specific migration history.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the required migration connection string is missing.
    /// </exception>
    /// <remarks>
    /// The caller is responsible for disposing the returned context.
    /// This method configures the context but does not execute migrations.
    /// </remarks>
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
