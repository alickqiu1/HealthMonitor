namespace HealthMonitor.Domain.Telemetry;

/// <summary>Canonical Edition 1.5 thresholds shared by health classification and incident observation policy.</summary>
/// <remarks>These fixed business rules must remain consistent; configuration must not silently change only one consumer.</remarks>
public static class TelemetryThresholds
{
    /// <summary>Maximum fresh observation age in seconds; exactly 90 seconds remains fresh.</summary>
    public const int OfflineAfterSeconds = 90;
    /// <summary>Maximum healthy latency in milliseconds; only strictly greater measurements violate the threshold.</summary>
    public const int LatencyWarningMs = 250;
    /// <summary>Maximum healthy CPU percentage; only strictly greater measurements violate the threshold.</summary>
    public const decimal CpuWarningPercent = 80m;
}
