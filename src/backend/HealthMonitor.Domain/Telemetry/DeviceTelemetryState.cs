
using System;

namespace HealthMonitor.Domain.Telemetry;

/// <summary>
/// Stores the latest telemetry observation for a device.
/// Owned exclusively by the Telemetry module.
/// </summary>
public sealed class DeviceTelemetryState
{
    public Guid DeviceId { get; private set; }

    public Guid LastPingId { get; private set; }

    public DateTimeOffset LastObservedAtUtc { get; private set; }

    public DateTimeOffset? LastSuccessfulAtUtc { get; private set; }

    public PingOutcome LatestOutcome { get; private set; }

    public int? LatestLatencyMs { get; private set; }

    public decimal? LatestCpuPercent { get; private set; }

    // Used by EF Core during materialization.
    private DeviceTelemetryState() { }

    private DeviceTelemetryState(TelemetryPing ping)
    {
        ApplyInitialPing(ping);
    }

    /// <summary>
    /// Creates the first telemetry state for a device.
    /// </summary>
    public static DeviceTelemetryState Create(TelemetryPing ping)
    {
        ArgumentNullException.ThrowIfNull(ping);

        return new DeviceTelemetryState(ping);
    }

    /// <summary>
    /// Applies an observation that is not older than
    /// the current observation.
    /// </summary>
    public void ApplyPing(TelemetryPing ping)
    {
        ArgumentNullException.ThrowIfNull(ping);

        if (ping.DeviceId != DeviceId)
        {
            throw new ArgumentException(
                "Ping belongs to a different device.",
                nameof(ping));
        }

        if (ping.Id == LastPingId)
        {
            throw new InvalidOperationException(
                "The latest ping has already been applied.");
        }

        if (ping.ObservedAtUtc < LastObservedAtUtc)
        {
            throw new InvalidOperationException(
                "Cannot apply an observation older than the current state.");
        }

        LastPingId = ping.Id;
        LastObservedAtUtc = ping.ObservedAtUtc;
        LatestOutcome = ping.Outcome;
        LatestLatencyMs = ping.LatencyMs;
        LatestCpuPercent = ping.CpuPercent;

        if (ping.Outcome == PingOutcome.Success)
        {
            LastSuccessfulAtUtc = ping.ObservedAtUtc;
        }
    }

    private void ApplyInitialPing(TelemetryPing ping)
    {
        DeviceId = ping.DeviceId;
        LastPingId = ping.Id;
        LastObservedAtUtc = ping.ObservedAtUtc;
        LatestOutcome = ping.Outcome;
        LatestLatencyMs = ping.LatencyMs;
        LatestCpuPercent = ping.CpuPercent;

        LastSuccessfulAtUtc =
            ping.Outcome == PingOutcome.Success
                ? ping.ObservedAtUtc
                : null;
    }
}
