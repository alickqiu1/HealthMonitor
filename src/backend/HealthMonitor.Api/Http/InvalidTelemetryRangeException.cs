namespace HealthMonitor.Api.Http;

/// <summary>Identifies caller range validation separately from provider/domain ArgumentException failures.</summary>
/// <remarks>Only the pre-query endpoint validation phase may create this exception. Clock and awaited port failures remain server failures.</remarks>
internal sealed class InvalidTelemetryRangeException : ArgumentException
{
    /// <summary>Preserves the validation cause internally while exposing only an owned message to HTTP error handling.</summary>
    /// <param name="cause">Endpoint validation failure, never a provider or server-clock failure.</param>
    internal InvalidTelemetryRangeException(ArgumentException cause)
        : base("Telemetry history requires a valid increasing UTC range of at most seven days.", "range", cause) { }
}
