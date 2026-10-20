using System.Net;
using Microsoft.AspNetCore.Http;

namespace HealthMonitor.Api.AiQueries;

/// <summary>Copies the original socket peer once per Kestrel connection, before HTTP middleware can rewrite it.</summary>
/// <remarks>Only the owned connection callback publishes this feature. Strings retain IPv6 scope without exposing
/// mutable IPAddress instances. Missing/non-IP endpoints remain unavailable instead of becoming trusted defaults.</remarks>
internal sealed class AiConnectionPeer
{
    /// <summary>Gets the copied canonical address, or null when no valid transport peer exists.</summary>
    internal string? Address { get; }
    /// <summary>Gets the original transport port; zero represents an unavailable endpoint.</summary>
    internal int Port { get; }
    private AiConnectionPeer(string? address, int port) { Address = address; Port = port; }

    /// <summary>Captures a valid original endpoint without resolving names or reading request headers.</summary>
    /// <param name="endpoint">Transport-owned remote endpoint, which may be absent or non-IP.</param>
    /// <returns>An immutable peer, unavailable when the transport cannot establish an IP/port.</returns>
    internal static AiConnectionPeer Capture(EndPoint? endpoint) => endpoint is IPEndPoint ip && ip.Port > 0
        ? new(AiClientIdentity.Canonical(ip.Address), ip.Port) : new(null, 0);

    /// <summary>Checks that no earlier HTTP middleware has already changed the transport peer identity.</summary>
    /// <param name="context">Current request before this application's own forwarding.</param>
    /// <returns>True only for the same canonical address and port captured at connection acceptance.</returns>
    internal bool Matches(HttpContext context) => Address is not null
        && Address == AiClientIdentity.Canonical(context.Connection.RemoteIpAddress) && Port == context.Connection.RemotePort;
}

/// <summary>Retains a verified client identity for one HTTP request, never as a singleton or connection-wide result.</summary>
/// <remarks>Later admission must use this snapshot before counter/provider work. It is not authentication or a proof
/// of an arbitrary host's topology. Producing unavailable metadata must not block ordinary dashboard reads.</remarks>
internal sealed class AiClientIdentity
{
    /// <summary>Gets the immutable unavailable marker for missing or previously rewritten transport context.</summary>
    internal static AiClientIdentity Unavailable { get; } = new(null);
    private readonly string? address;
    private AiClientIdentity(string? address) => this.address = address;
    /// <summary>Gets whether the owned ingress pipeline established an acceptable address.</summary>
    internal bool IsVerified => address is not null;

    /// <summary>Copies the final peer after owned forwarding; callers must have verified the original peer first.</summary>
    internal static AiClientIdentity Capture(IPAddress? client) => new(Canonical(client));

    /// <summary>Returns a detached address so a consumer cannot mutate this request's IPv6 scope.</summary>
    internal IPAddress? GetAddress() => address is null ? null : IPAddress.Parse(address);

    /// <summary>Canonicalizes mapped IPv4 and preserves IPv6 scope, rejecting unavailable wildcard/broadcast identities.</summary>
    internal static string? Canonical(IPAddress? client)
    {
        if (client is null) return null;
        if (client.IsIPv4MappedToIPv6) client = client.MapToIPv4();
        if (client.Equals(IPAddress.None) || client.GetAddressBytes().All(octet => octet == 0)) return null;
        return client.ToString();
    }
}