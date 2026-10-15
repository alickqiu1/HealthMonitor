
namespace HealthMonitor.Domain.Telemetry;

/// <summary>
/// Defines the outcome of a synthetic device health observation.
/// </summary>
/// <remarks>
/// An observation either succeeds with valid latency and CPU
/// measurements or times out without measurements.
///
/// This enum describes the observation result, not the device's
/// overall health classification. Health status is determined
/// separately by monitoring policy rules.
/// </remarks>
public enum PingOutcome
{
    /// <summary>
    /// The observation succeeded and contains latency and
    /// CPU utilization measurements.
    /// </summary>
    Success,

    /// <summary>
    /// The observation timed out and contains no latency
    /// or CPU utilization measurements.
    /// </summary>
    Timeout
}
