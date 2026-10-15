
using System;
using HealthMonitor.Infrastructure.Data.DeviceContext;
using HealthMonitor.Infrastructure.Data.TelemetryContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace HealthMonitor.Api;

/// <summary>
/// Resolves the privileged PostgreSQL connection used by
/// EF Core design-time migration operations.
/// </summary>
/// <remarks>
/// Two explicitly separated configuration paths are supported:
///
/// 1. Normal local development uses the API project's
///    ConnectionStrings:HealthMonitor User Secret.
///
/// 2. Authorized database integration testing uses the
///    ConnectionStrings__HealthMonitorIntegrationTest
///    environment variable.
///
/// CI must never fall back to development credentials.
/// In CI, the integration-test configuration is mandatory.
///
/// The CI connection is validated before either DbContext
/// is constructed. This prevents migrations from accidentally
/// targeting the hosted development database.
///
/// Runtime API connections are configured separately and
/// do not use these design-time factories.
/// </remarks>
internal static class DesignTimeDatabaseConfiguration
{
    private const string TestAuthorizationVariable =
        "HEALTHMONITOR_ENABLE_DB_TESTS";

    private const string TestConnectionVariable =
        "ConnectionStrings__HealthMonitorIntegrationTest";

    /// <summary>
    /// Resolves the connection string for EF Core migrations.
    /// </summary>
    /// <returns>
    /// A configured and non-empty PostgreSQL connection string.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the required environment-specific
    /// configuration is missing or invalid.
    /// </exception>
    /// <remarks>
    /// Explicit test authorization selects the isolated test
    /// database. GitHub Actions requires this authorization.
    ///
    /// Normal local development continues using User Secrets.
    /// A failed CI configuration must not fall back to those
    /// secrets or any runtime database connection.
    /// </remarks>
    public static string GetMigrationConnectionString()
    {
        bool isGitHubActions = string.Equals(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        bool testAuthorized = string.Equals(
            Environment.GetEnvironmentVariable(
                TestAuthorizationVariable),
            "true",
            StringComparison.Ordinal);

        if (isGitHubActions && !testAuthorized)
        {
            // CI must fail closed instead of silently selecting
            // privileged development migration credentials.
            throw new InvalidOperationException(
                "GitHub Actions requires explicit isolated " +
                "database-test authorization.");
        }

        if (testAuthorized)
        {
            // Explicit authorization selects the dedicated
            // test database, regardless of local User Secrets.
            //
            // An invalid test configuration must be rejected;
            // never fall back to the development database.
            return GetValidatedTestConnectionString();
        }

        // Preserve the existing local development behavior.
        // The privileged connection remains in User Secrets,
        // outside source control.
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<DeviceDbContextFactory>(
                optional: false)
            .Build();

        string? connectionString =
            configuration.GetConnectionString("HealthMonitor");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:HealthMonitor must be " +
                "configured for local EF Core migrations.");
        }

        return connectionString;
    }

    /// <summary>
    /// Validates and returns the dedicated PostgreSQL test
    /// connection supplied through an environment variable.
    /// </summary>
    /// <returns>
    /// A connection string restricted to the expected local
    /// PostgreSQL test database.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the connection is absent, malformed,
    /// or points outside the permitted test database.
    /// </exception>
    /// <remarks>
    /// Validation is intentionally stricter than ordinary
    /// database configuration.
    ///
    /// The database name, host, port, and database role
    /// must match the isolated test service provisioned
    /// by the integration-test workflow.
    ///
    /// This defense prevents accidental schema migration
    /// against a non-test PostgreSQL server.
    /// </remarks>
    private static string GetValidatedTestConnectionString()
    {
        string? connectionString =
            Environment.GetEnvironmentVariable(
                TestConnectionVariable);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"{TestConnectionVariable} must be configured " +
                "for authorized database integration testing.");
        }

        NpgsqlConnectionStringBuilder builder;

        try
        {
            builder = new NpgsqlConnectionStringBuilder(
                connectionString);
        }
        catch (ArgumentException exception)
        {
            // Do not include connection-string contents in
            // exception messages because they may contain
            // credentials.
            throw new InvalidOperationException(
                "The integration-test database connection " +
                "string is invalid.",
                exception);
        }

        bool validTarget =
            string.Equals(
                builder.Host,
                "127.0.0.1",
                StringComparison.Ordinal) &&
            builder.Port == 5432 &&
            string.Equals(
                builder.Database,
                "healthmonitor_test",
                StringComparison.Ordinal) &&
            string.Equals(
                builder.Username,
                "healthmonitor_test_user",
                StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(builder.Password);

        if (!validTarget)
        {
            // Do not silently accept localhost aliases,
            // additional hosts, or alternative database names.
            //
            // The test database is provisioned as an isolated
            // CI service with a known connection identity.
            throw new InvalidOperationException(
                "EF Core migrations require the authorized " +
                "isolated PostgreSQL test database at " +
                "127.0.0.1:5432/healthmonitor_test.");
        }

        return connectionString;
    }
}

/// <summary>
/// Creates the Device DbContext for EF Core design-time tooling.
/// </summary>
/// <remarks>
/// Supports Device schema migrations independently of normal
/// API runtime dependency injection.
///
/// The context uses migration credentials selected by
/// DesignTimeDatabaseConfiguration.
///
/// Device migrations maintain their own history in the
/// PostgreSQL device schema.
/// </remarks>
public sealed class DeviceDbContextFactory
    : IDesignTimeDbContextFactory<DeviceDbContext>
{
    /// <summary>
    /// Creates a configured Device DbContext for EF Core tooling.
    /// </summary>
    /// <param name="args">
    /// Design-time arguments supplied by EF Core tooling.
    /// Currently unused.
    /// </param>
    /// <returns>
    /// A Device DbContext configured for PostgreSQL migrations.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when migration database configuration is invalid.
    /// </exception>
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

                // Each bounded context maintains its own
                // migration history, preventing the Device
                // and Telemetry schemas from sharing state.
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
/// Supports Telemetry schema migrations independently of the
/// Device DbContext and runtime API configuration.
///
/// The context uses the shared, environment-aware migration
/// connection selection.
///
/// Telemetry migrations maintain their own history in the
/// PostgreSQL telemetry schema.
/// </remarks>
public sealed class TelemetryDbContextFactory
    : IDesignTimeDbContextFactory<TelemetryDbContext>
{
    /// <summary>
    /// Creates a configured Telemetry DbContext for EF Core tooling.
    /// </summary>
    /// <param name="args">
    /// Design-time arguments supplied by EF Core tooling.
    /// Currently unused.
    /// </param>
    /// <returns>
    /// A Telemetry DbContext configured for PostgreSQL migrations.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when migration database configuration is invalid.
    /// </exception>
    public TelemetryDbContext CreateDbContext(string[] args)
    {
        string connectionString =
            DesignTimeDatabaseConfiguration
                .GetMigrationConnectionString();

        var options =
            new DbContextOptionsBuilder<TelemetryDbContext>();

        options.UseNpgsql(
            connectionString,
            pg =>
            {
                pg.MigrationsAssembly(
                    "HealthMonitor.Infrastructure");

                // Telemetry has independent schema ownership
                // and must not share migration history with
                // the Device bounded context.
                pg.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "telemetry");
            });

        return new TelemetryDbContext(options.Options);
    }
}
