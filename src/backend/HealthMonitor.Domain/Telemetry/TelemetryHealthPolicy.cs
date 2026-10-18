namespace HealthMonitor.Domain.Telemetry;

/// <summary>Classifies a valid observation snapshot at one caller-captured UTC instant without reading time or writing incidents.</summary>
/// <remarks>
/// No observation means Unknown. Otherwise staleness takes priority, then Timeout, then successful measurement thresholds.
/// Retirement is intentionally outside this policy. Malformed input is rejected rather than interpreted as a health state.
/// </remarks>
public static class TelemetryHealthPolicy
{
    /// <summary>Computes health using the fixed Edition 1.5 freshness and measurement rules.</summary>
    /// <param name="asOfUtc">Non-default zero-offset UTC instant, captured once by the read request's composition service.</param>
    /// <param name="lastObservedAtUtc">Valid UTC observation time, or null only when every other observation field is null.</param>
    /// <param name="latestOutcome">Success or Timeout for an observation; null for valid absence.</param>
    /// <param name="latencyMs">Success latency in 0–60,000 milliseconds; null for Timeout or absence.</param>
    /// <param name="cpuPercent">Success CPU percentage in 0–100 with at most two decimal places; null for Timeout or absence.</param>
    /// <returns>Unknown, Offline, Degraded, or Healthy according to the priority rules.</returns>
    /// <exception cref="ArgumentException">Timestamps, observation presence, or outcome/measurement consistency is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An outcome or measurement is outside its supported range.</exception>
    /// <remarks>
    /// Equality at 90 seconds, 250 ms, and 80 percent remains fresh/healthy. A future observation is not stale under
    /// the literal age comparison: separate module reads may observe a commit after asOfUtc was captured. asOfUtc
    /// provides one evaluation instant, not a historical or cross-module serializable snapshot.
    /// Validation follows existing TelemetryPing invariants before classification, including for stale observations.
    /// </remarks>
    public static HealthState Classify(DateTimeOffset asOfUtc, DateTimeOffset? lastObservedAtUtc,
        PingOutcome? latestOutcome, int? latencyMs, decimal? cpuPercent)
    {
        RequireUtc(asOfUtc, nameof(asOfUtc));
        if (lastObservedAtUtc is null)
        {
            if (latestOutcome.HasValue || latencyMs.HasValue || cpuPercent.HasValue)
                throw new ArgumentException("An absent observation cannot contain an outcome or measurements.");
            return HealthState.Unknown;
        }
        RequireUtc(lastObservedAtUtc.Value, nameof(lastObservedAtUtc));
        if (latestOutcome is null)
            throw new ArgumentException("An observation requires an outcome.", nameof(latestOutcome));
        if (!Enum.IsDefined(latestOutcome.Value))
            throw new ArgumentOutOfRangeException(nameof(latestOutcome));
        if (latestOutcome == PingOutcome.Timeout)
        {
            if (latencyMs.HasValue || cpuPercent.HasValue)
                throw new ArgumentException("Timeout observations cannot contain measurements.");
        }
        else
        {
            if (!latencyMs.HasValue || !cpuPercent.HasValue)
                throw new ArgumentException("Successful observations require both measurements.");
            if (latencyMs.Value is < 0 or > 60000)
                throw new ArgumentOutOfRangeException(nameof(latencyMs));
            if (cpuPercent.Value is < 0m or > 100m)
                throw new ArgumentOutOfRangeException(nameof(cpuPercent));
            if (decimal.Round(cpuPercent.Value, 2) != cpuPercent.Value)
                throw new ArgumentException("CPU percentage must have at most two decimal places.", nameof(cpuPercent));
        }

        // Staleness is a read-time decision, not a scheduled state update or a new incident.
        if (asOfUtc - lastObservedAtUtc.Value > TimeSpan.FromSeconds(TelemetryThresholds.OfflineAfterSeconds))
            return HealthState.Offline;
        if (latestOutcome == PingOutcome.Timeout)
            return HealthState.Offline;
        if (latencyMs > TelemetryThresholds.LatencyWarningMs || cpuPercent > TelemetryThresholds.CpuWarningPercent)
            return HealthState.Degraded;
        return HealthState.Healthy;
    }

    /// <summary>Applies the same non-default, zero-offset timestamp requirement as persisted telemetry observations.</summary>
    /// <param name="value">Timestamp to validate.</param>
    /// <param name="parameterName">Owned argument name for diagnostics.</param>
    /// <exception cref="ArgumentException">The timestamp is default or has a nonzero offset.</exception>
    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
            throw new ArgumentException("A valid UTC timestamp is required.", parameterName);
    }
}
