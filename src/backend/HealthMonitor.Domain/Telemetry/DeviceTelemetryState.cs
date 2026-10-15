
using System;

namespace HealthMonitor.Domain.Telemetry;

/// <summary>
/// Maintains the latest persisted telemetry observation for a monitored device.
/// </summary>
/// <remarks>
/// This entity belongs exclusively to the Telemetry bounded context.
///
/// One state record exists per device that has received telemetry.
/// DeviceId is the persistent identity of the state record.
///
/// The state stores the latest observation and the timestamp of the
/// most recent successful observation. It supports efficient fleet
/// queries without scanning the complete telemetry history.
///
/// A timeout clears the latest latency and CPU measurements but
/// preserves LastSuccessfulAtUtc from an earlier successful observation.
///
/// Health classification, staleness evaluation, and incident lifecycle
/// decisions are performed by application and domain policy services.
/// This entity does not calculate a device's health status.
///
/// Device references are scalar identifiers. No foreign key or
/// navigation property to the Device bounded context is required.
/// </remarks>
public sealed class DeviceTelemetryState
{
    /// <summary>
    /// Gets the identifier of the device whose telemetry is tracked.
    /// </summary>
    /// <remarks>
    /// This is the primary key of the persisted telemetry state.
    /// </remarks>
    public Guid DeviceId { get; private set; }

    /// <summary>
    /// Gets the identifier of the most recently applied observation.
    /// </summary>
    public Guid LastPingId { get; private set; }

    /// <summary>
    /// Gets the UTC timestamp of the latest applied observation.
    /// </summary>
    public DateTimeOffset LastObservedAtUtc { get; private set; }

    /// <summary>
    /// Gets the UTC timestamp of the most recent successful observation.
    /// </summary>
    /// <remarks>
    /// Null when no successful observation has been applied.
    /// Timeout observations do not reset this value.
    /// </remarks>
    public DateTimeOffset? LastSuccessfulAtUtc { get; private set; }

    /// <summary>
    /// Gets the outcome of the latest applied observation.
    /// </summary>
    public PingOutcome LatestOutcome { get; private set; }

    /// <summary>
    /// Gets the latency of the latest successful observation,
    /// or null when the latest observation is a timeout.
    /// </summary>
    /// <remarks>
    /// This property describes the latest observation only.
    /// It does not retain an earlier successful latency value.
    /// </remarks>
    public int? LatestLatencyMs { get; private set; }

    /// <summary>
    /// Gets the CPU utilization of the latest successful observation,
    /// or null when the latest observation is a timeout.
    /// </summary>
    /// <remarks>
    /// This property describes the latest observation only.
    /// It does not retain an earlier successful CPU value.
    /// </remarks>
    public decimal? LatestCpuPercent { get; private set; }

    // Required by EF Core for entity materialization.
    // Application code creates state through the Create factory.
    private DeviceTelemetryState() { }

    /// <summary>
    /// Initializes telemetry state from the first observation.
    /// </summary>
    /// <param name="ping">Validated initial telemetry observation.</param>
    private DeviceTelemetryState(TelemetryPing ping)
    {
        ApplyInitialPing(ping);
    }

    /// <summary>
    /// Creates the initial telemetry state for a device.
    /// </summary>
    /// <param name="ping">
    /// First validated observation received for the device.
    /// </param>
    /// <returns>
    /// A new telemetry state initialized from the supplied observation.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when the observation is null.
    /// </exception>
    /// <remarks>
    /// A successful first observation initializes
    /// LastSuccessfulAtUtc to its observation timestamp.
    ///
    /// A timeout initializes LastSuccessfulAtUtc to null
    /// and leaves both latest measurements null.
    /// </remarks>
    public static DeviceTelemetryState Create(TelemetryPing ping)
    {
        ArgumentNullException.ThrowIfNull(ping);

        return new DeviceTelemetryState(ping);
    }

    /// <summary>
    /// Applies a new telemetry observation to the current device state.
    /// </summary>
    /// <param name="ping">
    /// Validated observation belonging to the same device.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when the observation is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the observation belongs to a different device.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the observation is the currently recorded ping
    /// or its timestamp precedes the latest recorded observation.
    /// </exception>
    /// <remarks>
    /// Observations must not be older than the current state.
    /// A different ping with an equal observation timestamp is
    /// currently accepted.
    ///
    /// A successful observation updates LastSuccessfulAtUtc.
    /// A timeout preserves the previous successful timestamp
    /// while clearing the latest measurements.
    ///
    /// Validation occurs before any state fields are modified,
    /// so a rejected observation leaves the state unchanged.
    /// </remarks>
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

    /// <summary>
    /// Initializes all state fields from the first validated observation.
    /// </summary>
    /// <param name="ping">Initial telemetry observation.</param>
    /// <remarks>
    /// This helper assumes the caller has already validated
    /// that the supplied observation is not null.
    /// </remarks>
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
