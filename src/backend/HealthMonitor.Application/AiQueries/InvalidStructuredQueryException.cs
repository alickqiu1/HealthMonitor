namespace HealthMonitor.Application.AiQueries;

/// <summary>Signals invalid proposal semantics without retaining provider output.</summary>
public sealed class InvalidStructuredQueryException : Exception
{
    /// <summary>Creates a fixed safe failure, distinct from request validation or provider transport failures.</summary>
    public InvalidStructuredQueryException() : base("The generated query proposal is invalid.") { }
}
