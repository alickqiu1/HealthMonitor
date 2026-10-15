
using HealthMonitor.Infrastructure.Data.DeviceContext;
using HealthMonitor.Infrastructure.Data.TelemetryContext;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// ------------------------------------------------------------
// 1. Shared PostgreSQL data source
// ------------------------------------------------------------

// Connection string is loaded from configuration.
// During local development, use .NET User Secrets.
// Normal API execution uses the restricted PostgreSQL account.
// EF Core migrations use separate design-time factories.
string? connectionString =
    builder.Configuration.GetConnectionString("HealthMonitorRuntime");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "ConnectionStrings:HealthMonitor must be configured.");
}

// One DI-owned singleton NpgsqlDataSource is shared
// by both bounded contexts.
builder.Services.AddSingleton<NpgsqlDataSource>(_ =>
{
    var dataSourceBuilder =
        new NpgsqlDataSourceBuilder(connectionString);

    return dataSourceBuilder.Build();
});

// ------------------------------------------------------------
// 2. Device bounded context
// ------------------------------------------------------------

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
// 3. Telemetry bounded context
// ------------------------------------------------------------

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
// 4. API services
// ------------------------------------------------------------

builder.Services.AddControllers();

// Built-in .NET 10 OpenAPI document generation.
builder.Services.AddOpenApi();

var app = builder.Build();

// ------------------------------------------------------------
// 5. HTTP middleware
// ------------------------------------------------------------

// Expose OpenAPI documentation during development.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();
