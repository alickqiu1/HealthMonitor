using HealthMonitor.Api.AiQueries;
using HealthMonitor.Api.Controllers;
using HealthMonitor.Api.Provisioning;
using HealthMonitor.Application.AiQueries;
using HealthMonitor.Application.AiQueries.Ports;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace HealthMonitor.Api.Composition;

/// <summary>Composes optional AI routes, trusted ingress and one lazily owned admitted runtime.</summary>
/// <remarks>AI is disabled by default. Enabling requires validated installation settings and separately provisioned
/// private budget storage; registration neither bootstraps storage nor verifies provider access.</remarks>
internal static class AiQueryApiComposition
{
    internal const string CorsPolicyName = "AiTranslation";

    /// <summary>Captures enablement once and shares the immutable snapshot across optional AI components.</summary>
    /// <param name="services">API services with exact-origin read CORS already registered.</param>
    /// <param name="configuration">Installation settings; disabled composition reads only enablement metadata.</param>
    /// <param name="environment">Actual host paths, required only when enabled.</param>
    /// <returns>The same collection.</returns>
    /// <exception cref="ArgumentNullException">A dependency is null.</exception>
    /// <exception cref="InvalidOperationException">Enablement or active installation settings are invalid.</exception>
    internal static IServiceCollection AddAiQueryApi(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment? environment = null) =>
        AddAiQueryApiCore(services, configuration, environment, null);

    /// <summary>Supplies a controlled disposable port for isolated effective-host acceptance, never public configuration.</summary>
    /// <param name="services">Services with read JSON/error contracts and exact-origin CORS.</param>
    /// <param name="configuration">Explicit installation configuration; no default providers are added here.</param>
    /// <param name="environment">Actual host paths used by enabled binding.</param>
    /// <param name="createProvider">Controlled factory whose returned disposable port transfers to the runtime owner.</param>
    /// <returns>The same services with conditional routes and lazy ownership.</returns>
    internal static IServiceCollection AddAiQueryApi(this IServiceCollection services, IConfiguration configuration,
        IWebHostEnvironment environment, Func<AiQueryOptions, IStructuredQueryGenerator> createProvider)
    {
        ArgumentNullException.ThrowIfNull(createProvider);
        return AddAiQueryApiCore(services, configuration, environment, createProvider);
    }

    /// <summary>Keeps routing and runtime/ingress settings consistent across configuration reloads.</summary>
    private static IServiceCollection AddAiQueryApiCore(IServiceCollection services, IConfiguration configuration,
        IWebHostEnvironment? environment, Func<AiQueryOptions, IStructuredQueryGenerator>? createProvider)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        // Never reread the flag: a reload must not turn disabled discovery into credential binding.
        var snapshot = AiQueryOptions.ReadEnabled(configuration)
            ? AiQueryOptions.BindEnabled(configuration, environment) : AiQueryOptions.Disabled;
        services.AddSingleton(snapshot);
        services.AddAiTrustedIngress(configuration);
        var mvc = services.AddControllers();
        if (!snapshot.Enabled)
            mvc.ConfigureApplicationPartManager(manager =>
                manager.FeatureProviders.Add(new AiQueryControllerFeatureProvider()));
        else if (createProvider is null) services.AddAiQueryRuntime(snapshot);
        else services.AddAiQueryRuntime(snapshot, createProvider);
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
