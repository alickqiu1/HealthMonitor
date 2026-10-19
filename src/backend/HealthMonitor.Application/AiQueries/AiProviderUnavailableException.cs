namespace HealthMonitor.Application.AiQueries;

/// <summary>Signals an unavailable provider without retaining its status body, credentials, URL, or raw exception.</summary>
public sealed class AiProviderUnavailableException : Exception
{
    /// <summary>Creates the fixed content-free transport failure.</summary>
    public AiProviderUnavailableException() : base("The AI provider is unavailable.") { }
}
