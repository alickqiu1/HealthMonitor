
using HealthMonitor.Api.Composition;
using HealthMonitor.Api.Provisioning;
using HealthMonitor.Infrastructure.Data.DeviceContext;
using HealthMonitor.Infrastructure.Data.TelemetryContext;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// ------------------------------------------------------------
// 1. Private device catalog provisioning
// ------------------------------------------------------------

// Provisioning is an explicitly invoked, local maintenance operation.
// It must never run during normal API startup or through an HTTP route.
//
// The command uses privileged database credentials and exits before
// registering or starting the normal HTTP application.
//
// Usage:
// dotnet run --project src/backend/HealthMonitor.Api -- --provision-devices
if (args.Length == 1 &&
    string.Equals(
        args[0],
        "--provision-devices",
        StringComparison.Ordinal))
{
    // The current provisioning command is restricted to local
    // development. Hosted provisioning will require a separately
    // secured operational workflow.
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "Local device provisioning requires Development mode.");
    }

    // Privileged credentials are used only by this explicit command.
    // Normal API requests use the restricted runtime account.
    string? provisioningConnectionString =
        builder.Configuration.GetConnectionString("HealthMonitor");

    if (string.IsNullOrWhiteSpace(provisioningConnectionString))
    {
        throw new InvalidOperationException(
            "ConnectionStrings:HealthMonitor must be configured.");
    }

    // This data source is never registered in the HTTP application's
    // dependency injection container. Disposal closes its pool.
    await using var provisioningDataSource =
        new NpgsqlDataSourceBuilder(
            provisioningConnectionString).Build();

    var provisioner =
        new DeviceCatalogProvisioner(provisioningDataSource);

    int insertedCount = await provisioner.ProvisionAsync();

    Console.WriteLine(
        $"Device catalog provisioning complete. " +
        $"Inserted {insertedCount} device(s).");

    return;
}

// ------------------------------------------------------------
// 2. Shared PostgreSQL runtime data source
// ------------------------------------------------------------

// Normal HTTP execution uses a restricted PostgreSQL account.
// The connection string is supplied through configuration, such as
// .NET User Secrets during development or hosting secrets in production.
//
// EF Core migrations use separate design-time DbContext factories
// with privileged credentials; migration access is not registered here.
string? connectionString =
    builder.Configuration.GetConnectionString("HealthMonitorRuntime");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "ConnectionStrings:HealthMonitorRuntime must be configured.");
}

// Both bounded contexts use the same DI-owned connection pool.
// Sharing the pool does not transfer table or migration ownership
// between the Device and Telemetry modules.
builder.Services.AddSingleton<NpgsqlDataSource>(_ =>
{
    var dataSourceBuilder =
        new NpgsqlDataSourceBuilder(connectionString);

    return dataSourceBuilder.Build();
});

// ------------------------------------------------------------
// 3. Device bounded context
// ------------------------------------------------------------

// DeviceDbContext owns the device schema and its independent
// EF Core migration history.
builder.Services.AddDbContext<DeviceDbContext>(
    (services, options) =>
    {
        var dataSource =
            services.GetRequiredService<NpgsqlDataSource>();

        options.UseNpgsql(
            dataSource,
            pg =>
            {
                pg.MigrationsAssembly(
                    "HealthMonitor.Infrastructure");

                pg.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "device");
            });
    });

// ------------------------------------------------------------
// 4. Telemetry bounded context
// ------------------------------------------------------------

// TelemetryDbContext owns the telemetry schema and its independent
// EF Core migration history. It uses the same runtime data source
// but does not depend on DeviceDbContext.
builder.Services.AddDbContext<TelemetryDbContext>(
    (services, options) =>
    {
        var dataSource =
            services.GetRequiredService<NpgsqlDataSource>();

        options.UseNpgsql(
            dataSource,
            pg =>
            {
                pg.MigrationsAssembly(
                    "HealthMonitor.Infrastructure");

                pg.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "telemetry");
            });
    });

// ------------------------------------------------------------
// 5. API services
// ------------------------------------------------------------

builder.Services.AddDeviceCatalog();

builder.Services.AddControllers();

// Register native ASP.NET Core OpenAPI document generation.
builder.Services.AddOpenApi();

var app = builder.Build();

// ------------------------------------------------------------
// 6. HTTP middleware and endpoints
// ------------------------------------------------------------

// OpenAPI documents are exposed only in Development.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();
