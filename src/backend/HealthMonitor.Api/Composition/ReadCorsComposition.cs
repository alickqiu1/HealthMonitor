namespace HealthMonitor.Api.Composition;

/// <summary>Registers the exact-origin browser policy for public read endpoints.</summary>
/// <remarks>
/// CORS controls browser response access, not authentication or server-side method enforcement.
/// Future AI or simulation POST routes require a separately reviewed policy rather than widening this read policy.
/// </remarks>
internal static class ReadCorsComposition
{
    internal const string PolicyName = "PublicReads";
    private const string OriginsKey = "Cors:AllowedOrigins";

    /// <summary>Validates origins immediately and registers a GET-only policy without credential support.</summary>
    /// <param name="services">API services; policy registration does not start a host or access a network.</param>
    /// <param name="configuration">Nonsecret exact origins. Missing or empty lists deny cross-origin response access.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="ArgumentNullException">Services or configuration is null.</exception>
    /// <exception cref="InvalidOperationException">An origin entry or the list shape is invalid.</exception>
    internal static IServiceCollection AddReadCors(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(OriginsKey);
        // The .NET 10 JSON provider represents [] as an empty scalar. Treat that safe empty value as deny-all.
        if (!string.IsNullOrEmpty(section.Value)) throw InvalidOrigins();
        var origins = section.GetChildren().Select((entry, index) =>
        {
            // Require the documented array shape; named, sparse, or nested entries must not silently become origins.
            if (entry.Key != index.ToString(System.Globalization.CultureInfo.InvariantCulture) || entry.GetChildren().Any())
                throw InvalidOrigins();
            return NormalizeOrigin(entry.Value);
        }).Distinct(StringComparer.Ordinal).ToArray();

        services.AddCors(options => options.AddPolicy(PolicyName, policy =>
            policy.WithOrigins(origins).WithMethods("GET").WithHeaders("Content-Type")));
        return services;
    }

    /// <summary>Normalizes scheme/host and default ports without accepting URL paths, credentials, or wildcard matching.</summary>
    /// <param name="value">One complete HTTP or HTTPS origin, without a trailing slash.</param>
    /// <returns>Canonical origin suitable for the framework's exact-origin policy builder.</returns>
    /// <exception cref="InvalidOperationException">The entry is not a bare HTTP(S) origin.</exception>
    private static string NormalizeOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Contains('*') || value.Contains('\\')
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString()
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw InvalidOrigins();

        // Uri treats both an omitted path and a trailing slash as "/". Reject the latter explicitly:
        // origins are authorities, and the framework does not match configured trailing-slash URLs.
        int authorityStart = value.IndexOf("://", StringComparison.Ordinal) + 3;
        if (authorityStart < 3 || value.AsSpan(authorityStart).Contains('/')) throw InvalidOrigins();
        // Uri removes default ports and retains IPv6 brackets/nondefault ports; the CORS builder also normalizes IDN hosts.
        return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped).ToLowerInvariant();
    }

    /// <summary>Reports the owned key only, never echoing potentially sensitive malformed configuration.</summary>
    private static InvalidOperationException InvalidOrigins() =>
        new($"{OriginsKey} must be a list of exact HTTP(S) origins without credentials, paths, query strings, fragments, or wildcards.");
}
