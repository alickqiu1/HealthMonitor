
namespace HealthMonitor.Domain.Telemetry;

/// <summary>
/// Defines the categories of device health incidents
/// recognized by the monitoring system.
/// </summary>
/// <remarks>
/// An incident represents a health condition detected
/// through synthetic telemetry observations.
///
/// The monitoring policy determines when an incident
/// should be opened or resolved. This enum identifies
/// the incident category only; it does not describe
/// the incident's lifecycle state.
///
/// Incident lifecycle information is maintained
/// by the IncidentLog entity.
/// </remarks>
public enum IncidentKind
{
    /// <summary>
    /// Indicates a device latency condition that violates
    /// the configured monitoring policy.
    /// </summary>
    Latency,

    /// <summary>
    /// Indicates a device CPU utilization condition that
    /// violates the configured monitoring policy.
    /// </summary>
    Cpu,

    /// <summary>
    /// Indicates a device connectivity condition associated
    /// with unsuccessful or missing observations.
    /// </summary>
    Connectivity
}
