
using System;

namespace HealthMonitor.Domain.Telemetry;

/// <summary>
/// Represents a committed simulation batch receipt used for
/// idempotency, auditing, and safe request replay.
/// </summary>
/// <remarks>
/// This entity belongs exclusively to the Telemetry bounded context.
///
/// Each receipt identifies a simulation request by its batch ID and
/// canonical request hash. The application service uses these values
/// to distinguish an original request, an identical replay, and a
/// conflicting request that reuses an existing batch ID.
///
/// A matching receipt allows the application to return the original
/// committed result without generating duplicate telemetry.
///
/// The receipt is persisted atomically with the telemetry observations,
/// device state updates, and incident changes produced by the batch.
///
/// This entity records a completed operation. It does not calculate
/// request hashes, execute simulations, enforce replay retention,
/// or manage database transactions.
/// </remarks>
public sealed class SimulationBatch
{
    /// <summary>
    /// Gets the unique identifier supplied for the simulation request.
    /// </summary>
    /// <remarks>
    /// Batch identity is used to locate an existing receipt during
    /// replay detection. The application layer enforces request-ID
    /// generation and replay-window policies.
    /// </remarks>
    public Guid BatchId { get; private set; }

    /// <summary>
    /// Gets the canonical request hash as lowercase hexadecimal text.
    /// </summary>
    /// <remarks>
    /// The value contains exactly 64 hexadecimal characters,
    /// representing a 256-bit request digest.
    ///
    /// The application service calculates the hash from the
    /// canonical scenario and sorted distinct device identifiers.
    /// This entity validates its representation but does not
    /// calculate or cryptographically verify the digest.
    /// </remarks>
    public string RequestHash { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the canonical simulation scenario identifier.
    /// </summary>
    /// <remarks>
    /// The value must be non-empty and contain no leading or
    /// trailing whitespace. The application layer determines
    /// which scenario values are supported.
    /// </remarks>
    public string Scenario { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the UTC timestamp associated with the original request.
    /// </summary>
    public DateTimeOffset RequestedAtUtc { get; private set; }

    /// <summary>
    /// Gets the UTC timestamp when the simulation batch was committed.
    /// </summary>
    /// <remarks>
    /// This timestamp must not precede RequestedAtUtc.
    /// </remarks>
    public DateTimeOffset CommittedAtUtc { get; private set; }

    /// <summary>
    /// Gets the number of devices affected by the committed batch.
    /// </summary>
    /// <remarks>
    /// The value must be non-negative. The entity permits zero;
    /// request-level validation determines whether an empty device
    /// selection is allowed for a particular simulation operation.
    /// </remarks>
    public int AffectedDeviceCount { get; private set; }

    // Required by EF Core for entity materialization.
    // Application code creates receipts through CreateCommitted.
    private SimulationBatch() { }

    /// <summary>
    /// Creates and validates a committed simulation batch receipt.
    /// </summary>
    /// <param name="batchId">Non-empty simulation batch identifier.</param>
    /// <param name="requestHash">
    /// Exactly 64 hexadecimal characters identifying the canonical request.
    /// </param>
    /// <param name="scenario">
    /// Non-empty canonical scenario identifier without surrounding whitespace.
    /// </param>
    /// <param name="requestedAtUtc">
    /// Non-default request timestamp with a zero UTC offset.
    /// </param>
    /// <param name="committedAtUtc">
    /// Non-default UTC commit timestamp that must not precede the request.
    /// </param>
    /// <param name="affectedDeviceCount">
    /// Non-negative number of affected devices.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when the batch identifier, request hash, scenario,
    /// or a timestamp violates the required format.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the affected device count is negative.
    /// </exception>
    /// <remarks>
    /// All inputs are validated before assigning entity state.
    /// Valid hexadecimal hash characters are normalized to lowercase.
    ///
    /// This constructor does not perform database operations or
    /// determine whether another receipt already exists.
    /// </remarks>
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
    /// Creates a completed simulation receipt for persistence with
    /// the corresponding telemetry changes.
    /// </summary>
    /// <param name="batchId">Non-empty batch identifier.</param>
    /// <param name="requestHash">
    /// Canonical 64-character hexadecimal request hash.
    /// </param>
    /// <param name="scenario">Canonical simulation scenario.</param>
    /// <param name="requestedAtUtc">Valid UTC request timestamp.</param>
    /// <param name="committedAtUtc">
    /// Valid UTC commit timestamp not earlier than the request.
    /// </param>
    /// <param name="affectedDeviceCount">
    /// Non-negative number of affected devices.
    /// </param>
    /// <returns>A validated committed simulation receipt.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when an identifier, hash, scenario, or timestamp is invalid.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the affected device count is negative.
    /// </exception>
    /// <remarks>
    /// The calling application service coordinates persistence of
    /// the receipt and all associated telemetry changes within
    /// one database transaction.
    /// </remarks>
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
