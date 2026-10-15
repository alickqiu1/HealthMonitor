
using System;
using System.Globalization;

namespace HealthMonitor.Application.Telemetry;

/// <summary>
/// Represents a rejected simulation batch identifier.
/// </summary>
/// <remarks>
/// Provides a stable error code that the API layer can translate
/// into a structured HTTP 400 response.
///
/// This exception represents an invalid new batch identifier.
/// It must not be used to reject an already committed, matching
/// receipt during replay processing.
/// </remarks>
public sealed class TelemetryBatchIdValidationException : ArgumentException
{
    /// <summary>
    /// Gets the machine-readable validation error code.
    /// </summary>
    /// <remarks>
    /// Supported values are:
    ///
    /// batch_id_invalid: Empty, non-UUIDv7, or invalid variant.
    /// batch_id_expired: Timestamp more than five minutes old.
    /// batch_id_future: Timestamp more than five minutes ahead.
    /// </remarks>
    public string Code { get; }

    /// <summary>
    /// Creates a validation exception with a stable error code.
    /// </summary>
    /// <param name="code">Machine-readable error code.</param>
    /// <param name="message">Human-readable failure explanation.</param>
    public TelemetryBatchIdValidationException(
        string code,
        string message)
        : base(message, "batchId")
    {
        Code = code;
    }
}

/// <summary>
/// Validates the UUIDv7 version and timestamp freshness of
/// identifiers for new telemetry simulation batches.
/// </summary>
/// <remarks>
/// Architecture Plan Edition 1.5 requires new batch identifiers
/// to use UUIDv7 with at most five minutes of clock skew.
///
/// UUIDv7 encodes a Unix timestamp in milliseconds within the
/// first 48 bits of the identifier.
///
/// This component extracts the timestamp directly from the
/// canonical GUID string representation, avoiding differences
/// between UUID byte order and the mixed-endian layout returned
/// by Guid.ToByteArray().
///
/// Freshness validation is performed only for new requests,
/// after the ingestion service has checked for an existing
/// receipt using the supplied BatchId.
///
/// An existing matching receipt may be replayed after its
/// identifier expires. Rejecting it here would violate the
/// receipt-first idempotency contract.
///
/// The embedded UUID timestamp does not determine when telemetry
/// was observed. Observation time is captured separately by
/// the server after acquiring the database write lock.
/// </remarks>
public sealed class TelemetryBatchIdValidator
{
    private const long MaximumClockSkewMilliseconds =
        5 * 60 * 1000;

    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes the validator with a controllable clock.
    /// </summary>
    /// <param name="timeProvider">
    /// Clock used to evaluate the freshness window.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when timeProvider is null.
    /// </exception>
    public TelemetryBatchIdValidator(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider
            ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Validates a batch identifier for a new ingestion request.
    /// </summary>
    /// <param name="batchId">Identifier to validate.</param>
    /// <exception cref="TelemetryBatchIdValidationException">
    /// Thrown when the identifier is not a valid UUIDv7,
    /// is more than five minutes old, or is more than
    /// five minutes in the future.
    /// </exception>
    /// <remarks>
    /// The permitted timestamp window is inclusive:
    ///
    /// now - 5 minutes &lt;= batch timestamp
    /// batch timestamp &lt;= now + 5 minutes
    ///
    /// Identifiers exactly on either boundary are accepted.
    ///
    /// The ingestion service must call this method only after
    /// confirming that no matching historical receipt exists.
    /// </remarks>
    public void ValidateNewBatch(Guid batchId)
    {
        if (batchId == Guid.Empty)
        {
            throw new TelemetryBatchIdValidationException(
                "batch_id_invalid",
                "BatchId must not be empty.");
        }

        // Guid.ToString("N") follows canonical UUID field order.
        // The first 12 hexadecimal characters contain the
        // 48-bit Unix timestamp, expressed in milliseconds.
        string value = batchId.ToString("N");

        // UUIDv7 uses version nibble 7 and the RFC 4122/
        // RFC 9562 variant (binary prefix 10).
        //
        // Check both fields before interpreting the timestamp.
        // An ordinary UUIDv4 must never pass merely because
        // its leading bits resemble a recent timestamp.
        bool validVersion = value[12] == '7';

        bool validVariant =
            value[16] is '8' or '9' or 'a' or 'b';

        if (!validVersion || !validVariant)
        {
            throw new TelemetryBatchIdValidationException(
                "batch_id_invalid",
                "BatchId must be a valid UUIDv7 identifier.");
        }

        long timestampMilliseconds = long.Parse(
            value.AsSpan(0, 12),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture);

        long currentMilliseconds =
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        // Compare integer Unix milliseconds rather than
        // converting the embedded timestamp to DateTimeOffset.
        //
        // The full 48-bit UUIDv7 timestamp range exceeds
        // DateTimeOffset's representable date range. Integer
        // comparison safely handles those unusual identifiers.
        //
        // Capture the current time once so both boundaries
        // are evaluated against the same reference instant.
        if (timestampMilliseconds <
            currentMilliseconds - MaximumClockSkewMilliseconds)
        {
            throw new TelemetryBatchIdValidationException(
                "batch_id_expired",
                "BatchId timestamp is more than five minutes old.");
        }

        if (timestampMilliseconds >
            currentMilliseconds + MaximumClockSkewMilliseconds)
        {
            throw new TelemetryBatchIdValidationException(
                "batch_id_future",
                "BatchId timestamp is more than five minutes in the future.");
        }

        // The identifier is valid for a new request.
        //
        // This does not establish catalog eligibility or commit
        // permission. Those checks remain the responsibility
        // of the ingestion application service.
    }
}
