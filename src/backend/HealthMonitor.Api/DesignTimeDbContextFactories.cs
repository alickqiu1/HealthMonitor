
using System;
using HealthMonitor.Api.Composition;
using HealthMonitor.Infrastructure.Data.DeviceContext;
using HealthMonitor.Infrastructure.Data.TelemetryContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace HealthMonitor.Api;

/// <summary>
/// Resolves the privileged PostgreSQL connection used by
/// EF Core design-time migration operations.
/// </summary>
/// <remarks>
/// Two explicitly separated configuration paths are supported:
///
/// 1. Explicit Development mode uses ConnectionStrings__HealthMonitor
///    before the API project's ConnectionStrings:HealthMonitor User Secret.
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
    /// Explicit Development uses a maintenance environment override before User Secrets.
    /// A failed CI configuration must not fall back to those
    /// secrets or any runtime database connection.
    /// </remarks>
    public static string GetMigrationConnectionString() =>
        GetMigrationConnectionString(Environment.GetEnvironmentVariable, LoadDevelopmentSecret);

    /// <summary>Resolves maintenance settings with controlled inputs, keeping secret loading lazy and testable.</summary>
    /// <param name="readEnvironment">Reads a named environment setting.</param>
    /// <param name="readSecret">Loads only the Development maintenance secret when no override exists.</param>
    /// <returns>Validated maintenance or isolated-test configuration; never runtime credentials.</returns>
    internal static string GetMigrationConnectionString(Func<string, string?> readEnvironment, Func<string?> readSecret)
    {
        bool isGitHubActions = string.Equals(
            readEnvironment("GITHUB_ACTIONS"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        bool testAuthorized = string.Equals(
            readEnvironment(TestAuthorizationVariable),
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
            return GetValidatedTestConnectionString(readEnvironment(TestConnectionVariable));
        }

        // WebApplication (.NET 10) gives DOTNET_ENVIRONMENT precedence over ASPNETCORE_ENVIRONMENT.
        // An empty/unknown higher-priority value is not permission to use Development credentials.
        string? environment = readEnvironment("DOTNET_ENVIRONMENT")
            ?? readEnvironment("ASPNETCORE_ENVIRONMENT");
        if (!string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Local EF Core migrations require explicit Development mode.");

        // Presence matters: an empty or invalid override must not silently select a different credential source.
        string? connectionString = readEnvironment("ConnectionStrings__HealthMonitor");
        if (connectionString is null)
        {
            try
            {
                connectionString = readSecret();
            }
            catch (Exception exception) when (exception is IOException or FormatException or InvalidOperationException)
            {
                // Configuration providers may quote malformed secret contents in diagnostics.
                throw new InvalidOperationException("The API Development maintenance User Secret could not be loaded; check its configuration.");
            }
        }

        return DatabaseConfiguration.ParseConnectionString(
            connectionString, "ConnectionStrings:HealthMonitor").ConnectionString;
    }

    /// <summary>Loads only the API maintenance secret, after explicit Development selection.</summary>
    private static string? LoadDevelopmentSecret()
    {
        using var configuration = new ConfigurationManager();
        configuration.AddUserSecrets<DeviceDbContextFactory>(optional: false);
        return configuration.GetConnectionString("HealthMonitor");
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
    private static string GetValidatedTestConnectionString(string? connectionString)
    {
        var builder = DatabaseConfiguration.ParseConnectionString(connectionString, TestConnectionVariable);

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

        return builder.ConnectionString;
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
    private readonly Func<string> _resolveConnectionString;

    /// <summary>Creates the factory with the approved environment-aware maintenance selection.</summary>
    public DeviceDbContextFactory() : this(DesignTimeDatabaseConfiguration.GetMigrationConnectionString) { }

    /// <summary>Provides controlled configuration selection for database-free factory verification.</summary>
    /// <param name="resolveConnectionString">Resolves approved maintenance settings without changing context ownership.</param>
    internal DeviceDbContextFactory(Func<string> resolveConnectionString) => _resolveConnectionString = resolveConnectionString;

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
            _resolveConnectionString();

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
    private readonly Func<string> _resolveConnectionString;

    /// <summary>Creates the factory with the approved environment-aware maintenance selection.</summary>
    public TelemetryDbContextFactory() : this(DesignTimeDatabaseConfiguration.GetMigrationConnectionString) { }

    /// <summary>Provides controlled configuration selection for database-free factory verification.</summary>
    /// <param name="resolveConnectionString">Resolves approved maintenance settings without changing context ownership.</param>
    internal TelemetryDbContextFactory(Func<string> resolveConnectionString) => _resolveConnectionString = resolveConnectionString;

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
            _resolveConnectionString();

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
