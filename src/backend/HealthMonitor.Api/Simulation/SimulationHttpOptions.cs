namespace HealthMonitor.Api.Simulation;

/// <summary>Captures the explicitly supported local simulator mode without loading an operator secret or enabling remote writes.</summary>
/// <param name="Enabled">True only after all static development/opt-in gates are validated.</param>
internal sealed record SimulationHttpOptions(bool Enabled)
{
    /// <summary>Reads only the two nonsecret flags; disabled mode leaves the controller absent.</summary>
    /// <param name="configuration">Explicit configuration, with absent flags defaulting to false.</param>
    /// <param name="environment">Resolved host environment.</param>
    /// <returns>Immutable startup mode; runtime bindings and actual connection IPs still require per-request checks.</returns>
    /// <exception cref="InvalidOperationException">A flag is malformed or an unsupported enablement mode is requested.</exception>
    internal static SimulationHttpOptions Bind(IConfiguration configuration, string environment)
    {
        bool enabled = Flag(configuration, "Simulation:HttpEndpointEnabled");
        bool anonymous = Flag(configuration, "Simulation:AllowLocalAnonymous");
        if (enabled && (!string.Equals(environment, Environments.Development, StringComparison.OrdinalIgnoreCase) || !anonymous))
            throw new InvalidOperationException("Simulation:HttpEndpointEnabled requires Development and Simulation:AllowLocalAnonymous; remote operator mode is not implemented.");
        return new(enabled);
    }

    /// <summary>Parses a flag without reflecting a malformed configuration value or retaining provider exceptions.</summary>
    private static bool Flag(IConfiguration configuration, string key)
    {
        string? value = configuration[key];
        if (value is null) return false;
        if (!bool.TryParse(value, out bool result)) throw new InvalidOperationException($"{key} must be true or false.");
        return result;
    }
}
