
namespace HealthMonitor.Domain.Telemetry;

/// <summary>
/// Types of incidents recognized by the health monitor.
/// </summary>
public enum IncidentKind
{
    Latency,
    Cpu,
    Connectivity
}
