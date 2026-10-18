
using HealthMonitor.Api.Composition;
using HealthMonitor.Api.Provisioning;
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
    // Normal API configuration must supply a separately provisioned restricted runtime account.
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

// Normal HTTP execution requires a restricted PostgreSQL account; actual grants are an operational prerequisite.
// The connection string is supplied through configuration, such as
// .NET User Secrets during development or hosting secrets in production.
//
// EF Core migrations use separate design-time DbContext factories
// with privileged credentials; migration access is not registered here.
DatabaseConfiguration.AddRuntimeDatabase(
    builder.Services, builder.Configuration, builder.Environment.EnvironmentName);

// ------------------------------------------------------------
// 3. API services
// ------------------------------------------------------------

builder.Services.AddDeviceCatalog();
builder.Services.AddTelemetryIngestion();
builder.Services.AddDemoProducer(builder.Configuration);
builder.Services.AddTelemetryRetention();

builder.Services.AddControllers();

// Register native ASP.NET Core OpenAPI document generation.
builder.Services.AddOpenApi();

var app = builder.Build();

// ------------------------------------------------------------
// 4. HTTP middleware and endpoints
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
