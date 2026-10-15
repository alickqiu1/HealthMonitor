
namespace HealthMonitor.Domain.Telemetry;

/// <summary>
/// Outcome of a synthetic device health observation.
/// </summary>
public enum PingOutcome
{
    Success,
    Timeout
}
