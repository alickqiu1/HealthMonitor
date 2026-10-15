
using System;

namespace HealthMonitor.Domain.Telemetry;

/// <summary>
/// Records a simulation request for idempotency,
/// auditing, and safe request replay.
/// </summary>
public sealed class SimulationBatch
{
    public Guid BatchId { get; private set; }

    public string RequestHash { get; private set; } = string.Empty;

    public string Scenario { get; private set; } = string.Empty;

    public DateTimeOffset RequestedAtUtc { get; private set; }

    public DateTimeOffset CommittedAtUtc { get; private set; }

    public int AffectedDeviceCount { get; private set; }

    // Used by EF Core during materialization.
    private SimulationBatch() { }

    private SimulationBatch(
        Guid batchId,
        string requestHash,
        string scenario,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset committedAtUtc,
        int affectedDeviceCount)
    {
        if (batchId == Guid.Empty)
            throw new ArgumentException(
                "Batch ID cannot be empty.",
                nameof(batchId));

        if (string.IsNullOrWhiteSpace(requestHash))
            throw new ArgumentException(
                "Request hash is required.",
                nameof(requestHash));

        string normalizedHash = requestHash.ToLowerInvariant();

        if (normalizedHash.Length != 64)
            throw new ArgumentException(
                "Request hash must contain exactly 64 characters.",
                nameof(requestHash));

        foreach (char character in normalizedHash)
        {
            if (!Uri.IsHexDigit(character))
                throw new ArgumentException(
                    "Request hash must be hexadecimal.",
                    nameof(requestHash));
        }

        if (string.IsNullOrWhiteSpace(scenario))
            throw new ArgumentException(
                "Simulation scenario is required.",
                nameof(scenario));

        if (scenario != scenario.Trim())
            throw new ArgumentException(
                "Simulation scenario must be canonical.",
                nameof(scenario));

        if (requestedAtUtc == default ||
            requestedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "A valid UTC request timestamp is required.",
                nameof(requestedAtUtc));
        }

        if (committedAtUtc == default ||
            committedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "A valid UTC commit timestamp is required.",
                nameof(committedAtUtc));
        }

        if (committedAtUtc < requestedAtUtc) 
        {
            throw new ArgumentException(
                "Commit timestamp cannot precede request timestamp.",
                nameof(committedAtUtc));
        }

        if (affectedDeviceCount < 0)
            throw new ArgumentOutOfRangeException(
                nameof(affectedDeviceCount),
                "Affected device count cannot be negative.");

        BatchId = batchId;
        RequestHash = normalizedHash;
        Scenario = scenario;
        RequestedAtUtc = requestedAtUtc;
        CommittedAtUtc = committedAtUtc;
        AffectedDeviceCount = affectedDeviceCount;
    }

    /// <summary>
    /// Creates a completed simulation receipt
    /// for atomic persistence with telemetry changes.
    /// </summary>
    public static SimulationBatch CreateCommitted(
        Guid batchId,
        string requestHash,
        string scenario,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset committedAtUtc,
        int affectedDeviceCount)
    {
        return new SimulationBatch(
            batchId,
            requestHash,
            scenario,
            requestedAtUtc,
            committedAtUtc,
            affectedDeviceCount);
    }
}
