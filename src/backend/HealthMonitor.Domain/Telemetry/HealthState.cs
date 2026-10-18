namespace HealthMonitor.Domain.Telemetry;

/// <summary>Read-time device health, separate from catalog retirement and recorded incident lifecycle.</summary>
/// <remarks>Numeric values are not the grid's severity sort order; query composition must apply its explicit ordering.</remarks>
public enum HealthState
{
    /// <summary>No telemetry observation is available.</summary>
    Unknown,
    /// <summary>The latest observation is fresh and successful, with both measurements within their healthy thresholds.</summary>
    Healthy,
    /// <summary>The latest observation is fresh and successful, with at least one measurement above its healthy threshold.</summary>
    Degraded,
    /// <summary>The latest observation is stale or its outcome is Timeout.</summary>
    Offline
}
