using System.Globalization;
using System.Threading.RateLimiting;
using HealthMonitor.Api.Controllers;
using HealthMonitor.Api.Http;
using HealthMonitor.Api.Simulation;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.RateLimiting;

namespace HealthMonitor.Api.Composition;

/// <summary>Composes the bounded local-only simulator; remote operator mode is intentionally unsupported.</summary>
internal static class SimulationApiComposition
{
    internal const string CorsPolicyName = "LocalSimulator";
    internal const string RatePolicyName = "Simulator";

    /// <summary>Registers conditional controller discovery and a separate POST policy without widening public read CORS.</summary>
    /// <param name="services">API services after read/CORS registration.</param>
    /// <param name="configuration">Only nonsecret simulator flags are consumed.</param>
    /// <param name="environment">Resolved host environment.</param>
    /// <returns>The same collection.</returns>
    /// <exception cref="InvalidOperationException">Configuration requests an invalid or unsupported simulator mode.</exception>
    internal static IServiceCollection AddSimulationApi(this IServiceCollection services, IConfiguration configuration, string environment)
    {
        var mode = SimulationHttpOptions.Bind(configuration, environment);
        services.AddSingleton(mode);
        services.AddScoped(_ => new SimulationBodyLimitFilter());
        services.AddControllers().ConfigureApplicationPartManager(manager =>
            manager.FeatureProviders.Add(new SimulationControllerFeatureProvider(mode.Enabled)));
        services.AddCors(options =>
        {
            if (!mode.Enabled) return;
            var reads = options.GetPolicy(ReadCorsComposition.PolicyName) ?? throw new InvalidOperationException("PublicReads CORS must be registered first.");
            options.AddPolicy(CorsPolicyName, policy => policy.WithOrigins(reads.Origins.ToArray()).WithMethods("POST").WithHeaders("Content-Type"));
        });
        services.AddRateLimiter(options =>
        {
            if (!mode.Enabled) return;
            // All admitted callers are the one local operator; do not create unbounded partitions from attacker-controlled headers.
            options.AddFixedWindowLimiter(RatePolicyName, limiter =>
            { limiter.PermitLimit = 5; limiter.Window = TimeSpan.FromMinutes(1); limiter.QueueLimit = 0; limiter.AutoReplenishment = true; });
            options.OnRejected = async (rejection, token) =>
            {
                double seconds = rejection.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retry) ? Math.Ceiling(retry.TotalSeconds) : 60;
                rejection.HttpContext.Response.Headers.RetryAfter = Math.Max(1, seconds).ToString(CultureInfo.InvariantCulture);
                await SimulationProblems.WriteAsync(rejection.HttpContext, 429, "simulation_rate_limited", "Simulation request limit reached.");
            };
        });
        return services;
    }

    /// <summary>Checks effective server bindings and actual connection IPs before rate consumption, binding, or ingestion.</summary>
    /// <param name="app">Pipeline after routing/CORS and before rate limiting/authorization.</param>
    /// <returns>The same pipeline.</returns>
    internal static IApplicationBuilder UseLocalSimulationAccess(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerTypeInfo.AsType() == typeof(SimulationController))
        {
            var mode = context.RequestServices.GetRequiredService<SimulationHttpOptions>();
            var server = context.RequestServices.GetService<IServer>();
            var bindings = server?.Features.Get<IServerAddressesFeature>()?.Addresses.ToArray();
            if (!LocalSimulationAccess.IsAllowed(mode.Enabled, bindings, context.Connection.LocalIpAddress, context.Connection.RemoteIpAddress))
            { await SimulationProblems.WriteAsync(context, 403, "simulation_local_only", "Local development access is required."); return; }
        }
        await next(context);
    });
}
