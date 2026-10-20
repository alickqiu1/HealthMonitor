using System.Globalization;
using System.Net;

namespace HealthMonitor.Api.AiQueries;

/// <summary>Captures explicit, bounded ingress trust without enabling forwarded-header processing.</summary>
/// <remarks>
/// This single-hop contract trusts only operator-supplied exact addresses; no network, hostname or implicit loopback trust is added.
/// Configuration parsing alone cannot undo framework/IIS/environment forwarding or establish a deployment's actual proxy topology.
/// Runtime middleware composition and verification remain separate requirements. No configuration reload changes this snapshot.
/// </remarks>
internal sealed class TrustedIngressOptions
{
    /// <summary>Gets an immutable no-trust snapshot without reading disabled ingress settings.</summary>
    internal static TrustedIngressOptions Disabled { get; } = new(false, []);

    /// <summary>Whether a later composition may process forwarding from the explicitly listed proxies.</summary>
    internal bool Enabled { get; }
    /// <summary>The supported maximum forwarded hop count, fixed at one for this increment.</summary>
    internal int ForwardLimit { get; }
    /// <summary>Canonical exact IP literals. Immutable strings avoid exposing mutable IPv6 scope identifiers.</summary>
    internal IReadOnlyList<string> KnownProxies { get; }

    /// <summary>Copies validated values so later configuration or caller mutation cannot widen trust.</summary>
    private TrustedIngressOptions(bool enabled, string[] proxies)
    { Enabled = enabled; ForwardLimit = 1; KnownProxies = Array.AsReadOnly((string[])proxies.Clone()); }

    /// <summary>Reads only the nonsecret TrustedIngress section and validates supplied values even when disabled.</summary>
    /// <param name="configuration">Configuration with Enabled, KnownProxies and ForwardLimit leaf/list settings.</param>
    /// <returns>A disabled, empty-trust snapshot when omitted; enabling requires explicit one-hop and proxy settings.</returns>
    /// <exception cref="ArgumentNullException">Configuration is null.</exception>
    /// <exception cref="InvalidOperationException">A supplied shape/value is invalid or enabled trust is incomplete.</exception>
    /// <remarks>
    /// IConfiguration can collapse missing, JSON null and empty objects into the same childless null section.
    /// Missing/null/empty KnownProxies (including the JSON provider's empty-array scalar) mean an empty list, allowed only when disabled.
    /// No raw JSON token-shape guarantee is made after that abstraction; indexed entries must still be contiguous nonnull leaves.
    /// </remarks>
    internal static TrustedIngressOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var root = configuration.GetSection("TrustedIngress");
        var children = root.GetChildren().ToArray();
        if (root.Value is not null || children.Any(child => !new[] { "Enabled", "KnownProxies", "ForwardLimit" }.Contains(child.Key, StringComparer.OrdinalIgnoreCase)))
            throw Invalid("TrustedIngress");

        bool Has(string key) => children.Any(child => child.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        bool enabled = false;
        var flag = root.GetSection("Enabled");
        if (Has("Enabled") && (flag.GetChildren().Any() || flag.Value is null || flag.Value != flag.Value.Trim() || !bool.TryParse(flag.Value, out enabled)))
            throw Invalid("TrustedIngress:Enabled");
        var limit = root.GetSection("ForwardLimit");
        // Fixed, explicit one-hop enablement prevents accidentally trusting an undocumented chain.
        if ((Has("ForwardLimit") && (limit.GetChildren().Any() || limit.Value != "1")) || (enabled && !Has("ForwardLimit")))
            throw Invalid("TrustedIngress:ForwardLimit");

        var list = root.GetSection("KnownProxies");
        var entries = list.GetChildren().Take(17).ToArray();
        if (!string.IsNullOrEmpty(list.Value) || entries.Length > 16 || (enabled && entries.Length == 0))
            throw Invalid("TrustedIngress:KnownProxies");
        var proxies = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry.Key != index.ToString(CultureInfo.InvariantCulture) || entry.GetChildren().Any())
                throw Invalid("TrustedIngress:KnownProxies");
            string address = NormalizeAddress(entry.Value);
            if (!unique.Add(address)) throw Invalid("TrustedIngress:KnownProxies");
            proxies.Add(address);
        }
        return new(enabled, proxies.ToArray());
    }

    /// <summary>Rejects platform-dependent/legacy spellings before normalizing exact address identity.</summary>
    private static string NormalizeAddress(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw != raw.Trim() || raw.Any(char.IsWhiteSpace)
            || raw.IndexOfAny(['[', ']', '/', '\\']) >= 0)
            throw Invalid("TrustedIngress:KnownProxies");
        bool ipv6 = raw.Contains(':');
        if (!ipv6 && !IsDottedDecimal(raw)) throw Invalid("TrustedIngress:KnownProxies");
        int zone = raw.IndexOf('%');
        if (zone >= 0)
        {
            string scope = raw[(zone + 1)..];
            // Numeric zones avoid DNS/interface lookup and retain an explicit scope in native IPv6 identity.
            if (!ipv6 || !uint.TryParse(scope, NumberStyles.None, CultureInfo.InvariantCulture, out uint value)
                || scope != value.ToString(CultureInfo.InvariantCulture)) throw Invalid("TrustedIngress:KnownProxies");
        }
        string withoutZone = zone < 0 ? raw : raw[..zone];
        if (ipv6 && withoutZone.Contains('.') && !IsDottedDecimal(withoutZone[(withoutZone.LastIndexOf(':') + 1)..]))
            throw Invalid("TrustedIngress:KnownProxies");
        if (!IPAddress.TryParse(raw, out var address)) throw Invalid("TrustedIngress:KnownProxies");
        // Mapped IPv4 and ordinary IPv4 must not create distinct trust entries for the same peer.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        // Unspecified IPv6 remains unspecified with a scope; address equality alone includes ScopeId.
        if (address.GetAddressBytes().All(octet => octet == 0) || address.Equals(IPAddress.None))
            throw Invalid("TrustedIngress:KnownProxies");
        return address.ToString();
    }

    /// <summary>Requires four ordinary decimal octets, excluding integer, shorthand, hexadecimal and leading-zero forms.</summary>
    private static bool IsDottedDecimal(string value)
    {
        var octets = value.Split('.');
        return octets.Length == 4 && octets.All(octet => octet.Length is >= 1 and <= 3
            && (octet.Length == 1 || octet[0] != '0') && octet.All(character => character is >= '0' and <= '9')
            && byte.TryParse(octet, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }

    /// <summary>Reports an owned key without echoing malformed values or unknown user-supplied key names.</summary>
    private static InvalidOperationException Invalid(string key) => new($"{key} must contain explicit supported single-hop proxy configuration.");
}
