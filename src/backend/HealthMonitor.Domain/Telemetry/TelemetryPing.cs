
using System;

namespace HealthMonitor.Domain.Telemetry;

/// <summary>
/// Represents one synthetic health observation
/// for a monitored device.
/// </summary>
public sealed class TelemetryPing
{
    public Guid Id { get; private set; }
    public Guid DeviceId { get; private set; }
    public Guid BatchId { get; private set; }

    public DateTimeOffset ObservedAtUtc { get; private set; }

    public PingOutcome Outcome { get; private set; }

    public int? LatencyMs { get; private set; }
    public decimal? CpuPercent { get; private set; }

    // Used by EF Core during materialization.
    private TelemetryPing() { }

    private TelemetryPing(
        Guid id,
        Guid deviceId,
        Guid batchId,
        DateTimeOffset observedAtUtc,
        PingOutcome outcome,
        int? latencyMs,
        decimal? cpuPercent)
    {
        if (id == Guid.Empty)
            throw new ArgumentException(
                "Ping ID cannot be empty.", nameof(id));

        if (deviceId == Guid.Empty)
            throw new ArgumentException(
                "Device ID cannot be empty.", nameof(deviceId));

        if (batchId == Guid.Empty)
            throw new ArgumentException(
                "Batch ID cannot be empty.", nameof(batchId));

        if (observedAtUtc == default ||
            observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "A valid UTC observation timestamp is required.",
                nameof(observedAtUtc));
        }

        if (!Enum.IsDefined(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome));

        if (outcome == PingOutcome.Timeout)
        {
            if (latencyMs.HasValue || cpuPercent.HasValue)
            {
                throw new ArgumentException(
                    "Timeout observations cannot contain measurements.");
            }
        }
        else
        {
            if (!latencyMs.HasValue || !cpuPercent.HasValue)
            {
                throw new ArgumentException(
                    "Successful observations require both measurements.");
            }

            if (latencyMs.Value < 0 || latencyMs.Value > 60000)
                throw new ArgumentOutOfRangeException(nameof(latencyMs));

            if (cpuPercent.Value < 0m || cpuPercent.Value > 100m)
                throw new ArgumentOutOfRangeException(nameof(cpuPercent));

            if (decimal.Round(cpuPercent.Value, 2) != cpuPercent.Value)
            {
                throw new ArgumentException(
                    "CPU percentage must have at most two decimal places.",
                    nameof(cpuPercent));
            }
        }

        Id = id;
        DeviceId = deviceId;
        BatchId = batchId;
        ObservedAtUtc = observedAtUtc;
        Outcome = outcome;
        LatencyMs = latencyMs;
        CpuPercent = cpuPercent;
    }

    public static TelemetryPing CreateSuccess(
        Guid id,
        Guid deviceId,
        Guid batchId,
        DateTimeOffset observedAtUtc,
        int latencyMs,
        decimal cpuPercent)
    {
        return new TelemetryPing(
            id,
            deviceId,
            batchId,
            observedAtUtc,
            PingOutcome.Success,
            latencyMs,
            cpuPercent);
    }

    public static TelemetryPing CreateTimeout(
        Guid id,
        Guid deviceId,
        Guid batchId,
        DateTimeOffset observedAtUtc)
    {
        return new TelemetryPing(
            id,
            deviceId,
            batchId,
            observedAtUtc,
            PingOutcome.Timeout,
            null,
            null);
    }
}
