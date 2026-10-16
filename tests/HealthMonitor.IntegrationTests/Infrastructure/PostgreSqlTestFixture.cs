
using Npgsql;

namespace HealthMonitor.IntegrationTests.Infrastructure;

/// <summary>
/// Provides validated PostgreSQL connections for database integration tests.
/// </summary>
/// <remarks>
/// Integration tests must never use the application's normal Supabase
/// database or its runtime and migration connection strings.
///
/// The fixture requires an explicit opt-in, a dedicated database name,
/// and a local PostgreSQL host. These safeguards prevent accidental
/// execution against the project's existing hosted database.
///
/// This fixture does not create, modify, or delete database objects.
/// Individual tests are responsible for isolating their own data.
/// </remarks>
internal sealed class PostgreSqlTestFixture
{
    private const string EnableTestsVariable =
        "HEALTHMONITOR_ENABLE_DB_TESTS";

    private const string RequiredDatabaseName =
        "healthmonitor_test";

    /// <summary>
    /// Opens a connection to the explicitly authorized test database.
    /// </summary>
    /// <param name="cancellationToken">
    /// Token used to cancel the connection operation.
    /// </param>
    /// <returns>
    /// An open PostgreSQL connection owned by the caller.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when integration tests are not explicitly enabled,
    /// or the configured database fails the isolation checks.
    /// </exception>
    /// <remarks>
    /// The caller must dispose the returned connection.
    ///
    /// Opening a connection does not authorize destructive operations.
    /// Integration tests must still use isolated records and transactions.
    /// </remarks>
    public async Task<NpgsqlConnection> OpenConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        ValidateExplicitOptIn();

        string connectionString =
            IntegrationTestDatabaseConfiguration.GetConnectionString();

        var connectionSettings =
            new NpgsqlConnectionStringBuilder(connectionString);

        ValidateConnectionTarget(connectionSettings);

        var connection = new NpgsqlConnection(connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken);

            await VerifyIdentityAsync(connection, cancellationToken);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Creates a caller-owned data source verified through one of its own connections.</summary>
    /// <param name="cancellationToken">Cancellation for initial connection and identity verification.</param>
    /// <returns>A data source which the caller must dispose after all provisioner operations finish.</returns>
    /// <exception cref="InvalidOperationException">Authorization, target, or actual identity is invalid.</exception>
    /// <remarks>
    /// Build from the original validated settings, not an opened connection's potentially
    /// password-redacted ConnectionString. This does not enable credential persistence or logging.
    /// Every connection from this source uses the same validated local configuration.
    /// </remarks>
    public async Task<NpgsqlDataSource> CreateDataSourceAsync(CancellationToken cancellationToken = default)
    {
        ValidateExplicitOptIn();
        string connectionString = IntegrationTestDatabaseConfiguration.GetConnectionString();
        ValidateConnectionTarget(new NpgsqlConnectionStringBuilder(connectionString));
        var source = NpgsqlDataSource.Create(connectionString);
        try
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await VerifyIdentityAsync(connection, cancellationToken);
            return source;
        }
        catch
        {
            await source.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Requires explicit authorization before accessing test configuration.
    /// </summary>
    private static void ValidateExplicitOptIn()
    {
        string? enabled =
            Environment.GetEnvironmentVariable(EnableTestsVariable);

        if (!string.Equals(
                enabled,
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "PostgreSQL integration tests are disabled. " +
                $"Set {EnableTestsVariable}=true to enable them.");
        }
    }

    /// <summary>
    /// Rejects database targets outside the approved local test setup.
    /// </summary>
    private static void ValidateConnectionTarget(
        NpgsqlConnectionStringBuilder settings)
    {
        if (!string.Equals(
                settings.Database,
                RequiredDatabaseName,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Integration tests require database " +
                $"'{RequiredDatabaseName}'.");
        }

        // The initial implementation supports local PostgreSQL
        // and GitHub Actions services exposed through localhost.
        // Additional hosts require an explicit reviewed change.
        //
        // Host may be null when configuration is incomplete.
        // Null and unapproved hosts must fail the allowlist check
        // rather than falling back to an unintended server.
        string? host = settings.Host;

        if (host is not ("localhost" or "127.0.0.1" or "::1"))
        {
            throw new InvalidOperationException(
                "Integration tests require a local PostgreSQL host.");
        }
    }
    /// <summary>Checks the connected identity rather than trusting configuration alone.</summary>
    private static async Task VerifyIdentityAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT current_database()", connection);
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(result as string, RequiredDatabaseName, StringComparison.Ordinal))
            throw new InvalidOperationException("Connected to an unexpected PostgreSQL database.");
    }
}
