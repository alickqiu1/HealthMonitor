
using HealthMonitor.Api.Provisioning;
using HealthMonitor.IntegrationTests.Infrastructure;
using Npgsql;
using Xunit;

namespace HealthMonitor.IntegrationTests.Provisioning;

/// <summary>
/// Verifies device catalog provisioning against a real, isolated
/// PostgreSQL database.
/// </summary>
/// <remarks>
/// These tests require a dedicated PostgreSQL database named
/// healthmonitor_test and explicit execution authorization.
///
/// They must never run against the normal development or
/// production Supabase database.
///
/// Tests are currently opt-in and must be executed separately
/// from the default integration test suite until database setup
/// and cleanup are fully implemented.
/// </remarks>
public sealed class DeviceCatalogProvisionerTests
{
    /// <summary>
    /// Verifies that provisioning an empty catalog inserts all
    /// 24 fixed fictional devices.
    /// </summary>
    /// <remarks>
    /// This test requires an isolated database with the Device
    /// migrations already applied and an empty device catalog.
    ///
    /// The test deliberately performs no cleanup. Database
    /// initialization and cleanup will be added to the fixture
    /// before enabling automated execution.
    /// </remarks>
    [Fact(Skip = "Requires isolated PostgreSQL database and test setup.")]
    public async Task ProvisionAsync_EmptyCatalog_Inserts24Devices()
    {
        var fixture = new PostgreSqlTestFixture();

        await using var connection =
            await fixture.OpenConnectionAsync();

        await using var dataSource =
            NpgsqlDataSource.Create(connection.ConnectionString);

        var provisioner =
            new DeviceCatalogProvisioner(dataSource);

        int insertedCount =
            await provisioner.ProvisionAsync();

        Assert.Equal(24, insertedCount);

        await using var verificationConnection =
            await dataSource.OpenConnectionAsync();

        await using var command = new NpgsqlCommand(
            """
            SELECT COUNT(*)
            FROM device.monitored_devices
            """,
            verificationConnection);

        long actualCount =
            (long)(await command.ExecuteScalarAsync()
                ?? throw new InvalidOperationException(
                    "Device count query returned no result."));

        Assert.Equal(24L, actualCount);
    }
}
