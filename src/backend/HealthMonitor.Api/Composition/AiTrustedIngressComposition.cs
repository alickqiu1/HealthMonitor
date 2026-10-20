using System.Net;
using HealthMonitor.Api.AiQueries;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace HealthMonitor.Api.Composition;

/// <summary>Composes explicit Kestrel peer capture and narrowly trusted HTTP forwarding without activating AI.</summary>
/// <remarks>The application owns its single endpoint-default callback; it changes no listen/protocol/TLS settings.
/// A later replacement that omits capture makes AI identity unavailable. No IIS/server reflection or restoration of
/// previously rewritten peers is attempted. Real production-composition acceptance remains an activation prerequisite.</remarks>
internal static class AiTrustedIngressComposition
{
    /// <summary>Registers lazy ingress settings and a connection callback only when the captured AI snapshot is enabled.</summary>
    /// <param name="services">Services already containing the immutable AI options snapshot.</param>
    /// <param name="configuration">Nonsecret trusted-ingress configuration, never read while AI is disabled.</param>
    /// <returns>The same collection; no socket, provider, counter or file is opened.</returns>
    internal static IServiceCollection AddAiTrustedIngress(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton(provider => provider.GetRequiredService<AiQueryOptions>().Enabled
            ? TrustedIngressOptions.FromConfiguration(configuration) : TrustedIngressOptions.Disabled);
        services.AddOptions<KestrelServerOptions>().Configure<AiQueryOptions>((server, ai) =>
        {
            if (!ai.Enabled) return;
            // Capture before TLS/HTTP and copy only immutable peer facts, shared safely by multiplexed requests.
            server.ConfigureEndpointDefaults(endpoint => endpoint.Use(next => connection =>
            {
                connection.Features.Set(AiConnectionPeer.Capture(connection.RemoteEndPoint));
                return next(connection);
            }));
        });
        return services;
    }

    /// <summary>Verifies original peer facts before owned forwarding and publishes a separate final identity per request.</summary>
    /// <param name="app">Pipeline before HTTPS redirection and routing.</param>
    /// <param name="configuration">Actual root configuration containing the framework automatic-forwarding key.</param>
    /// <returns>The same builder, unchanged while AI is disabled.</returns>
    /// <exception cref="InvalidOperationException">Active automatic-forwarding configuration is unsafe or malformed.</exception>
    internal static IApplicationBuilder UseAiTrustedIngress(this IApplicationBuilder app, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(app); ArgumentNullException.ThrowIfNull(configuration);
        if (!app.ApplicationServices.GetRequiredService<AiQueryOptions>().Enabled) return app;
        RejectAutomaticForwarding(configuration);
        var trust = app.ApplicationServices.GetRequiredService<TrustedIngressOptions>();
        return app.Use(next =>
        {
            RequestDelegate capture = context =>
            {
                context.Features.Set(AiClientIdentity.Capture(context.Connection.RemoteIpAddress));
                return next(context);
            };
            RequestDelegate verified = capture;
            if (trust.Enabled)
            {
                var options = new ForwardedHeadersOptions
                {
                    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
                    ForwardLimit = 1, RequireHeaderSymmetry = true
                };
                // An explicit local object cannot be widened by global ConfigureOptions or framework defaults.
                options.KnownIPNetworks.Clear(); options.KnownProxies.Clear();
                foreach (string proxy in trust.KnownProxies) options.KnownProxies.Add(IPAddress.Parse(proxy));
                verified = new ForwardedHeadersMiddleware(capture,
                    app.ApplicationServices.GetRequiredService<ILoggerFactory>(), Options.Create(options)).Invoke;
            }
            return context =>
            {
                // Never leave a stale marker from a previous request or an unrecognized hosting component.
                context.Features.Set(AiClientIdentity.Unavailable);
                var original = context.Features.Get<AiConnectionPeer>();
                if (original is null || !original.Matches(context)) return next(context);
                return verified(context);
            };
        });
    }

    /// <summary>Rejects the framework's broad startup forwarding mode before using this application's explicit policy.</summary>
    /// <remarks>The ASPNETCORE_ environment prefix maps to this exact IConfiguration key. Null/absence are equivalent
    /// after IConfiguration and mean off in framework 10.0.11; a supplied empty, nested or unknown value is rejected.
    /// An earlier HTTP address/port rewrite is additionally detected through captured peer facts. This is not a general hosting audit.</remarks>
    private static void RejectAutomaticForwarding(IConfiguration configuration)
    {
        const string key = "ForwardedHeaders_Enabled";
        var flag = configuration.GetSection(key);
        if (flag.GetChildren().Any() || (flag.Value is { } raw && !raw.Equals("false", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("ForwardedHeaders_Enabled must remain false for explicit AI ingress.");
    }
}