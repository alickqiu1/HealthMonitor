using System.Net;

namespace HealthMonitor.Api.Simulation;

/// <summary>Evaluates effective listener and connection facts, never request Host or forwarded headers.</summary>
/// <remarks>Unknown effective bindings fail closed. Pure tests with fabricated facts are not proof of a deployed host's effective binding.</remarks>
internal static class LocalSimulationAccess
{
    /// <summary>Requires the validated development mode, exclusively loopback listeners, and actual loopback connection endpoints.</summary>
    /// <param name="enabled">Validated immutable startup opt-in.</param>
    /// <param name="bindings">Snapshot of IServerAddressesFeature addresses; missing/empty evidence is denied.</param>
    /// <param name="local">Actual local connection address.</param>
    /// <param name="remote">Actual remote connection address.</param>
    /// <returns>True only when every independent gate is established.</returns>
    internal static bool IsAllowed(bool enabled, IReadOnlyCollection<string>? bindings, IPAddress? local, IPAddress? remote) =>
        enabled && bindings is { Count: > 0 } && bindings.All(IsLoopbackBinding) && IsLoopback(local) && IsLoopback(remote);

    /// <summary>Accounts for IPv4-mapped IPv6 without trusting proxy-supplied address claims.</summary>
    private static bool IsLoopback(IPAddress? address) => address is not null
        && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    /// <summary>Rejects wildcard, mixed-host, malformed, and unsupported listener evidence.</summary>
    private static bool IsLoopbackBinding(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString()
            || (uri.Scheme != "http" && uri.Scheme != "https") || uri.UserInfo.Length != 0
            || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IsLoopback(address));
    }
}
