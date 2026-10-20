
using HealthMonitor.Api.AiQueries;
using HealthMonitor.Api.Composition;
using HealthMonitor.Api.Provisioning;
using Npgsql;

// Reject a misplaced maintenance flag before builder argument parsing can route it into ordinary startup.
bool provisionAiBudget = args.Contains("--provision-ai-budget", StringComparer.Ordinal);
if (provisionAiBudget && args.Length != 1)
{
    Console.Error.WriteLine("Use --provision-ai-budget as the sole command argument.");
    Environment.ExitCode = 1;
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Explicit filesystem maintenance exits before database registration or HTTP/worker startup.
// CreateBuilder still loads the normal configuration providers; the helper reads only the counter-path value.
if (provisionAiBudget)
{
    try
    {
        await AiBudgetProvisioner.CreateAsync(builder.Configuration, builder.Environment, TimeProvider.System);
        Console.WriteLine("AI budget counter created. AI enablement is a separate operation.");
    }
    catch (InvalidOperationException error)
    {
        // Only the helper's owned fixed diagnostic is emitted, without original exceptions or filesystem paths.
        Console.Error.WriteLine(error.Message);
        Environment.ExitCode = 1;
    }
    return;
}

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
builder.Services.AddFleetReads(builder.Configuration);
builder.Services.AddTelemetryIngestion();
builder.Services.AddDemoProducer(builder.Configuration);
builder.Services.AddTelemetryRetention();

builder.Services.AddReadApi();
builder.Services.AddReadCors(builder.Configuration);
builder.Services.AddOperationalProbes();
builder.Services.AddSimulationApi(builder.Configuration, builder.Environment.EnvironmentName);
builder.Services.AddAiQueryApi(builder.Configuration, builder.Environment);

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

app.UseReadApiErrors();
app.UseAiTrustedIngress(builder.Configuration);
app.UseHttpsRedirection();
app.UseRouting();
app.UseAiQueryNoStore();
// Browser response access is evaluated after routing and before endpoint authorization.
app.UseCors(ReadCorsComposition.PolicyName);
app.UseLocalSimulationAccess();
app.UseRateLimiter();
app.UseAuthorization();
app.MapControllers();
app.MapOperationalProbes();

app.Run();
