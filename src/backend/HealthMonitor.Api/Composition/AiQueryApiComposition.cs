using HealthMonitor.Api.AiQueries;
using HealthMonitor.Api.Controllers;
using HealthMonitor.Api.Provisioning;
using HealthMonitor.Application.AiQueries;
using HealthMonitor.Application.AiQueries.Ports;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace HealthMonitor.Api.Composition;

/// <summary>Registers the AI HTTP contract while keeping production routing explicitly disabled.</summary>
/// <remarks>The temporary startup gate must be reviewed when provider transport and all admission controls exist.
/// Contract tests activate the controller only in their isolated host; this is not enabled production behavior.</remarks>
internal static class AiQueryApiComposition
{
    internal const string CorsPolicyName = "AiTranslation";

    /// <summary>Validates the disabled leaf flag and registers lazy Application composition without a generator.</summary>
    /// <param name="services">API services with exact-origin read CORS already registered.</param>
    /// <param name="configuration">Only the nonsecret AiQueries:Enabled flag is inspected.</param>
    /// <returns>The same collection.</returns>
    /// <exception cref="ArgumentNullException">A dependency is null.</exception>
    /// <exception cref="InvalidOperationException">The flag is malformed or attempts premature enablement.</exception>
    internal static IServiceCollection AddAiQueryApi(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        // Keep premature activation ahead of active option/credential binding until every admission gate is composed.
        if (AiQueryOptions.ReadEnabled(configuration))
            throw new InvalidOperationException("AiQueries:Enabled cannot be enabled until provider transport and all admission controls are implemented.");
        // A second flag read could observe a reload and bind credentials after the disabled guard passed.
        services.AddSingleton(AiQueryOptions.Disabled);
        services.AddAiTrustedIngress(configuration);
        services.AddControllers().ConfigureApplicationPartManager(manager =>
            manager.FeatureProviders.Add(new AiQueryControllerFeatureProvider()));
        services.AddScoped<AiQueryBodyLimitFilter>();
        services.AddSingleton(_ => new QueryVocabulary(FixedDeviceCatalog.Devices.Select(d => d.SiteCode),
            FixedDeviceCatalog.Devices.Select(d => d.DeviceType)));
        // The absent generator must never be resolved by disabled startup or controller discovery.
        services.AddScoped(provider => new TranslateDeviceQuery(provider.GetRequiredService<IStructuredQueryGenerator>(),
            provider.GetRequiredService<QueryVocabulary>()));
        services.AddCors(options =>
        {
            var reads = options.GetPolicy(ReadCorsComposition.PolicyName)
                ?? throw new InvalidOperationException("Exact-origin read CORS must be registered before AI CORS.");
            options.AddPolicy(CorsPolicyName, policy => policy.WithOrigins(reads.Origins.ToArray())
                .WithMethods("POST").WithHeaders("Content-Type"));
        });
        return services;
    }

    /// <summary>Sets no-store at response start for matched AI endpoints, including filter failures and cleared exceptions.</summary>
    /// <param name="app">Pipeline after routing and before CORS/controllers.</param>
    /// <returns>The same pipeline; absent disabled routes retain the ordinary 404 policy.</returns>
    internal static IApplicationBuilder UseAiQueryNoStore(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var action = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        if (action?.ControllerTypeInfo.AsType() == typeof(AiDeviceQueriesController))
            context.Response.OnStarting(() => { context.Response.Headers.CacheControl = "no-store"; return Task.CompletedTask; });
        await next();
    });
}
