
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace HealthMonitor.IntegrationTests.Infrastructure;

/// <summary>
/// Loads and validates the connection settings for isolated PostgreSQL
/// integration tests.
/// </summary>
/// <remarks>
/// Integration tests may create tables and modify records. They must use
/// a dedicated disposable test database, never the development or
/// production Supabase database.
///
/// Database availability is not required for compiling the test project
/// or running unit tests.
/// </remarks>
internal static class IntegrationTestDatabaseConfiguration
{
    private const string ConnectionStringName =
        "HealthMonitorIntegrationTest";

    /// <summary>
    /// Gets the explicitly configured integration-test connection string.
    /// </summary>
    /// <returns>
    /// A sensitive connection string passing the preliminary database-name check; never log it.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the test connection is missing or fails the preliminary database-name check.
    /// </exception>
    /// <exception cref="ArgumentException">Npgsql cannot parse the supplied connection string.</exception>
    /// <remarks>
    /// This loader reads environment configuration only, never runtime or maintenance secrets.
    /// Its substring check is preliminary: PostgreSqlTestFixture must also enforce opt-in,
    /// the exact database name, a loopback host, and the connected database name before use.
    /// Parsing alone neither opens a connection nor establishes exclusive database ownership.
    /// </remarks>
    public static string GetConnectionString()
    {
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        string? connectionString =
            configuration.GetConnectionString(ConnectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:HealthMonitorIntegrationTest " +
                "must identify a dedicated test database.");
        }

        var builder =
            new NpgsqlConnectionStringBuilder(connectionString);

        // Require a clearly identified disposable database.
        // Never accept the default Supabase 'postgres' database.
        if (string.IsNullOrWhiteSpace(builder.Database) ||
            !builder.Database.Contains(
                "healthmonitor_test",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Integration tests require a database name " +
                "containing 'healthmonitor_test'.");
        }

        return connectionString;
    }
}
