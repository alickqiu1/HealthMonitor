using HealthMonitor.Api;
using HealthMonitor.Api.Composition;
using HealthMonitor.Infrastructure.Data.DeviceContext;
using HealthMonitor.Infrastructure.Data.TelemetryContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;

namespace HealthMonitor.UnitTests.Configuration;

/// <summary>Verifies configuration decisions using fictional inputs, without secrets, host startup, or database connections.</summary>
public sealed class DatabaseConfigurationTests
{
    private const string RuntimeKey = "ConnectionStrings:HealthMonitorRuntime";
    private const string MaintenanceEnvironmentKey = "ConnectionStrings__HealthMonitor";
    private const string TestEnvironmentKey = "ConnectionStrings__HealthMonitorIntegrationTest";
    private const string AuthorizationKey = "HEALTHMONITOR_ENABLE_DB_TESTS";
    private const string Valid = "Host=example.invalid;Database=fictional;Username=fake_user;Password=fake_marker";
    private const string Test = "Host=127.0.0.1;Port=5432;Database=healthmonitor_test;Username=healthmonitor_test_user;Password=fake_marker";

    private static IConfiguration Config(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder().AddInMemoryCollection(entries.Select(e =>
            new KeyValuePair<string, string?>(e.Key, e.Value))).Build();

    private static string Runtime(string? value, string environment = "Development") =>
        DatabaseConfiguration.GetRuntimeConnectionString(Config((RuntimeKey, value)), environment);

    private static string Migration(Dictionary<string, string?> environment, Func<string?>? secret = null) =>
        DesignTimeDatabaseConfiguration.GetMigrationConnectionString(
            key => environment.GetValueOrDefault(key),
            secret ?? (() => throw new InvalidOperationException("Secret loader must not run.")));

    private static void AssertSanitized(Action action)
    {
        var error = Assert.Throws<InvalidOperationException>(action);
        Assert.DoesNotContain("fake_marker", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("fake_marker")]
    [InlineData("Host=example.invalid;Password=fake_marker;Unknown=fake_marker")]
    [InlineData("Host=example.invalid;Database=fictional;Username=fake_user;Port=fake_marker")]
    [InlineData("Database=fictional;Username=fake_user;Password=fake_marker")]
    [InlineData("Host=example.invalid;Username=fake_user;Password=fake_marker")]
    [InlineData("Host=example.invalid;Database=fictional;Password=fake_marker")]
    public void Runtime_InvalidOrIncompleteConfiguration_FailsSafely(string? value) =>
        AssertSanitized(() => Runtime(value));

    [Theory]
    [InlineData("Device")]
    [InlineData("Telemetry")]
    [InlineData("HealthMonitor")]
    [InlineData("HealthMonitorIntegrationTest")]
    public void Runtime_OtherPurposeOrLegacyKey_NeverSubstitutes(string key)
    {
        var configuration = Config(($"ConnectionStrings:{key}", Valid));
        var error = Assert.Throws<InvalidOperationException>(() =>
            DatabaseConfiguration.GetRuntimeConnectionString(configuration, "Development"));
        Assert.Contains(RuntimeKey, error.Message);
    }

    [Fact]
    public void Runtime_ValidCanonicalSetting_IgnoresConflictingUnusedKeys()
    {
        var configuration = Config((RuntimeKey, Valid),
            ("ConnectionStrings:Device", "invalid"), ("ConnectionStrings:Telemetry", "different"),
            ("ConnectionStrings:HealthMonitor", "privileged"), ("ConnectionStrings:HealthMonitorIntegrationTest", Test));
        Assert.Equal("example.invalid", new NpgsqlConnectionStringBuilder(
            DatabaseConfiguration.GetRuntimeConnectionString(configuration, "Development")).Host);
    }

    [Theory]
    [InlineData("")]
    [InlineData(";Maximum Pool Size=10")]
    [InlineData(";MaxPoolSize=10")]
    public void Runtime_ApprovedOrOmittedPoolSize_UsesTen(string suffix) =>
        Assert.Equal(10, new NpgsqlConnectionStringBuilder(Runtime(Valid + suffix)).MaxPoolSize);

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(100)]
    public void Runtime_DifferentExplicitPoolSize_Fails(int size) =>
        AssertSanitized(() => Runtime(Valid + $";Maximum Pool Size={size}"));

    [Fact]
    public void Runtime_MinimumPoolSizeBeyondBudget_FailsBeforeDataSourceConstruction() =>
        AssertSanitized(() => Runtime(Valid + ";Minimum Pool Size=11"));

    [Fact]
    public void Runtime_MinimumPoolSizeAtBudget_IsAccepted() =>
        Assert.Equal(10, new NpgsqlConnectionStringBuilder(Runtime(Valid + ";Minimum Pool Size=10")).MinPoolSize);

    [Fact]
    public void Runtime_ProductionWithoutExplicitTls_FailsWithTlsGuidance()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Runtime(Valid, "Production"));
        Assert.Contains("VerifyFull", error.Message);
    }
    [Theory]
    [InlineData("Production", "Disable")]
    [InlineData("Production", "Prefer")]
    [InlineData("Production", "Require")]
    [InlineData("Production", "VerifyCA")]
    [InlineData("Staging", "Require")]
    [InlineData("", "Require")]
    public void Runtime_OutsideDevelopment_RequiresVerifyFull(string environment, string sslMode) =>
        AssertSanitized(() => Runtime(Valid + $";SSL Mode={sslMode}", environment));

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Development")]
    [InlineData("development")]
    public void Runtime_VerifyFull_IsAcceptedWithoutOpeningConnection(string environment) =>
        Assert.Equal(SslMode.VerifyFull, new NpgsqlConnectionStringBuilder(
            Runtime(Valid + ";SSL Mode=VerifyFull", environment)).SslMode);

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void Runtime_TransactionPoolerPort_IsRejected(string environment) =>
        AssertSanitized(() => Runtime(Valid + ";Port=6543;SSL Mode=VerifyFull", environment));

    [Fact]
    public void Runtime_ValidationOccursBeforeRegistration()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            DatabaseConfiguration.AddRuntimeDatabase(services, Config(), "Development"));
        Assert.Empty(services);
    }

    [Fact]
    public void Runtime_TwoScopedContexts_UseOneSingletonAndSeparateHistories()
    {
        var services = new ServiceCollection();
        DatabaseConfiguration.AddRuntimeDatabase(services, Config((RuntimeKey, Valid)), "Development");
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services,
            d => d.ServiceType == typeof(NpgsqlDataSource)).Lifetime);
        Assert.All(services.Where(d => d.ServiceType == typeof(DeviceDbContext)
            || d.ServiceType == typeof(TelemetryDbContext)), d => Assert.Equal(ServiceLifetime.Scoped, d.Lifetime));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var source = provider.GetRequiredService<NpgsqlDataSource>();
        var device = first.ServiceProvider.GetRequiredService<DeviceDbContext>();
        var telemetry = first.ServiceProvider.GetRequiredService<TelemetryDbContext>();
        Assert.Same(device, first.ServiceProvider.GetRequiredService<DeviceDbContext>());
        Assert.NotSame(device, second.ServiceProvider.GetRequiredService<DeviceDbContext>());
        Assert.NotSame(telemetry, second.ServiceProvider.GetRequiredService<TelemetryDbContext>());
        // Provider-specific option inspection proves object identity without a connection or private-field reflection.
#pragma warning disable EF1001 // The pinned provider exposes its configured data source on this options extension.
        Assert.Same(source, device.GetService<IDbContextOptions>().FindExtension<NpgsqlOptionsExtension>()!.DataSource);
        Assert.Same(source, telemetry.GetService<IDbContextOptions>().FindExtension<NpgsqlOptionsExtension>()!.DataSource);
#pragma warning restore EF1001
        AssertHistory(device, "device");
        AssertHistory(telemetry, "telemetry");
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("Production", "Development", false)]
    [InlineData("Staging", "Development", false)]
    [InlineData("", "Development", false)]
    [InlineData("Development", "Production", true)]
    [InlineData(null, "Development", true)]
    [InlineData("development", null, true)]
    public void Migration_EnvironmentPrecedence_IsExplicit(string? dotnet, string? aspnet, bool allowed)
    {
        var environment = new Dictionary<string, string?>
        {
            ["DOTNET_ENVIRONMENT"] = dotnet, ["ASPNETCORE_ENVIRONMENT"] = aspnet,
            [MaintenanceEnvironmentKey] = Valid
        };
        if (allowed)
            Assert.Equal("fictional", new NpgsqlConnectionStringBuilder(Migration(environment)).Database);
        else
            Assert.Throws<InvalidOperationException>(() => Migration(environment));
    }

    [Fact]
    public void Migration_DevelopmentOverride_WinsWithoutLoadingSecrets()
    {
        var environment = new Dictionary<string, string?>
        {
            ["DOTNET_ENVIRONMENT"] = "Development", [MaintenanceEnvironmentKey] = Valid
        };
        Assert.Equal("fake_user", new NpgsqlConnectionStringBuilder(Migration(environment)).Username);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("fake_marker")]
    [InlineData("Host=example.invalid;Database=fictional;Username=fake_user;Port=fake_marker")]
    [InlineData("Host=example.invalid;Database=fictional;Password=fake_marker")]
    public void Migration_InvalidOverride_DoesNotLoadSecrets(string value) =>
        AssertSanitized(() => Migration(new()
        {
            ["DOTNET_ENVIRONMENT"] = "Development", [MaintenanceEnvironmentKey] = value
        }));

    [Fact]
    public void Migration_DevelopmentWithoutOverride_LoadsOnlyMaintenanceSecret()
    {
        int calls = 0;
        var result = Migration(new() { ["ASPNETCORE_ENVIRONMENT"] = "Development" },
            () => { calls++; return Valid; });
        Assert.Equal(1, calls);
        Assert.Equal("fictional", new NpgsqlConnectionStringBuilder(result).Database);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fake_marker")]
    public void Migration_InvalidSecret_FailsSafely(string? secret) =>
        AssertSanitized(() => Migration(new() { ["DOTNET_ENVIRONMENT"] = "Development" }, () => secret));

    [Theory]
    [InlineData("io")]
    [InlineData("format")]
    [InlineData("configuration")]
    public void Migration_SecretProviderFailure_IsSanitized(string kind)
    {
        Exception error = kind switch
        {
            "io" => new IOException("fake_marker"),
            "format" => new FormatException("fake_marker"),
            _ => new InvalidOperationException("fake_marker")
        };
        AssertSanitized(() => Migration(new() { ["DOTNET_ENVIRONMENT"] = "Development" }, () => throw error));
    }
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("")]
    public void Migration_NonDevelopment_DoesNotLoadSecrets(string environment) =>
        Assert.Throws<InvalidOperationException>(() =>
            Migration(new() { ["DOTNET_ENVIRONMENT"] = environment }));

    [Fact]
    public void Migration_RuntimeAndLegacyKeys_AreNotFallbacks()
    {
        var environment = new Dictionary<string, string?>
        {
            ["DOTNET_ENVIRONMENT"] = "Development",
            ["ConnectionStrings__HealthMonitorRuntime"] = Valid,
            ["ConnectionStrings__Device"] = Valid, ["ConnectionStrings__Telemetry"] = Valid
        };
        Assert.Throws<InvalidOperationException>(() => Migration(environment, () => null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("True")]
    public void Migration_GitHubActions_RequiresExactTestAuthorization(string? authorization) =>
        Assert.Throws<InvalidOperationException>(() => Migration(new()
        {
            ["GITHUB_ACTIONS"] = "TRUE", [AuthorizationKey] = authorization,
            ["DOTNET_ENVIRONMENT"] = "Development", [MaintenanceEnvironmentKey] = Valid,
            [TestEnvironmentKey] = Test
        }));

    [Fact]
    public void Migration_AuthorizedTests_OverrideEveryLocalSelection()
    {
        var result = Migration(new()
        {
            ["GITHUB_ACTIONS"] = "true", [AuthorizationKey] = "true", [TestEnvironmentKey] = Test,
            ["DOTNET_ENVIRONMENT"] = "Production", [MaintenanceEnvironmentKey] = "invalid"
        });
        Assert.Equal("healthmonitor_test", new NpgsqlConnectionStringBuilder(result).Database);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fake_marker")]
    [InlineData("Host=example.invalid;Database=healthmonitor_test;Username=healthmonitor_test_user;Password=fake_marker")]
    [InlineData("Host=127.0.0.1,example.invalid;Database=healthmonitor_test;Username=healthmonitor_test_user;Password=fake_marker")]
    [InlineData("Host=localhost;Database=healthmonitor_test;Username=healthmonitor_test_user;Password=fake_marker")]
    [InlineData("Host=::1;Database=healthmonitor_test;Username=healthmonitor_test_user;Password=fake_marker")]
    [InlineData("Host=127.0.0.1;Port=5433;Database=healthmonitor_test;Username=healthmonitor_test_user;Password=fake_marker")]
    [InlineData("Host=127.0.0.1;Database=healthmonitor_test_extra;Username=healthmonitor_test_user;Password=fake_marker")]
    [InlineData("Host=127.0.0.1;Database=HEALTHMONITOR_TEST;Username=healthmonitor_test_user;Password=fake_marker")]
    [InlineData("Host=127.0.0.1;Database=healthmonitor_test;Username=wrong;Password=fake_marker")]
    [InlineData("Host=127.0.0.1;Database=healthmonitor_test;Username=healthmonitor_test_user")]
    public void Migration_InvalidAuthorizedTestTarget_NeverFallsBack(string? value) =>
        AssertSanitized(() => Migration(new()
        {
            [AuthorizationKey] = "true", [TestEnvironmentKey] = value,
            ["DOTNET_ENVIRONMENT"] = "Development", [MaintenanceEnvironmentKey] = Valid
        }));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Factories_UseSharedSelectionAndKeepIndependentHistories(bool testMode)
    {
        var environment = new Dictionary<string, string?> { ["DOTNET_ENVIRONMENT"] = "Development" };
        if (testMode)
        {
            environment[AuthorizationKey] = "true";
            environment[TestEnvironmentKey] = Test;
        }
        else environment[MaintenanceEnvironmentKey] = Valid;
        string Resolve() => Migration(environment);
        using var device = new DeviceDbContextFactory(Resolve).CreateDbContext([]);
        using var telemetry = new TelemetryDbContextFactory(Resolve).CreateDbContext([]);
        var expected = new NpgsqlConnectionStringBuilder(testMode ? Test : Valid).ConnectionString;
        Assert.Equal(expected, device.Database.GetConnectionString());
        Assert.Equal(expected, telemetry.Database.GetConnectionString());
        AssertHistory(device, "device");
        AssertHistory(telemetry, "telemetry");
        Assert.Equal(System.Data.ConnectionState.Closed, device.Database.GetDbConnection().State);
        Assert.Equal(System.Data.ConnectionState.Closed, telemetry.Database.GetDbConnection().State);
    }

    [Fact]
    public void Factories_InvalidSelection_FailsBeforeContextCreation()
    {
        string Resolve() => Migration(new() { ["GITHUB_ACTIONS"] = "true" });
        Assert.Throws<InvalidOperationException>(() => new DeviceDbContextFactory(Resolve).CreateDbContext([]));
        Assert.Throws<InvalidOperationException>(() => new TelemetryDbContextFactory(Resolve).CreateDbContext([]));
    }

    private static void AssertHistory(DbContext context, string schema)
    {
        var options = context.GetService<IDbContextOptions>().Extensions.OfType<RelationalOptionsExtension>().Single();
        Assert.Equal("__ef_migrations_history", options.MigrationsHistoryTableName);
        Assert.Equal(schema, options.MigrationsHistoryTableSchema);
        Assert.Equal("HealthMonitor.Infrastructure", options.MigrationsAssembly);
    }
}
