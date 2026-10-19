namespace HealthMonitor.Application.AiQueries;

/// <summary>Signals expiry of the owned provider budget, distinct from caller cancellation.</summary>
public sealed class AiProviderTimeoutException : Exception
{
    /// <summary>Creates the fixed content-free timeout failure.</summary>
    public AiProviderTimeoutException() : base("The AI provider request timed out.") { }
}
