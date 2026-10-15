
using System;
using System.Globalization;
using HealthMonitor.Application.Telemetry;
using Xunit;

namespace HealthMonitor.UnitTests.Telemetry;

/// <summary>
/// Verifies UUIDv7 version, variant, and timestamp validation
/// for new synthetic telemetry ingestion batches.
/// </summary>
/// <remarks>
/// Tests cover valid UUIDv7 identifiers, invalid UUID versions
/// and variants, exact five-minute freshness boundaries,
/// and identifiers one millisecond outside either boundary.
///
/// An injected deterministic TimeProvider ensures that
/// freshness tests do not depend on the system clock.
///
/// This suite tests identifier validation only. Receipt-first
/// replay ordering, database transactions, and catalog
/// eligibility are verified separately at the ingestion layer.
///
/// No external services or database connections are required.
/// </remarks>
public sealed class TelemetryBatchIdValidatorTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Provides a deterministic UTC clock for validation tests.
    /// </summary>
    /// <remarks>
    /// The production validator reads time through TimeProvider,
    /// so a fixed clock makes freshness boundary tests repeatable
    /// without delays or time-sensitive assertions.
    /// </remarks>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        /// <summary>
        /// Initializes the clock at a fixed UTC instant.
        /// </summary>
        /// <param name="utcNow">UTC instant returned by the clock.</param>
        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        /// <summary>
        /// Returns the configured UTC instant.
        /// </summary>
        /// <returns>The fixed UTC instant.</returns>
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    /// <summary>
    /// Constructs a deterministic UUID with a UUIDv7-compatible
    /// millisecond timestamp and configurable version and variant.
    /// </summary>
    /// <param name="timestamp">
    /// Timestamp encoded into the first 48 UUID bits.
    /// </param>
    /// <param name="version">
    /// Version nibble, normally 7 for UUIDv7.
    /// </param>
    /// <param name="variant">
    /// First hexadecimal nibble of the RFC UUID variant field,
    /// normally 8, 9, a, or b.
    /// </param>
    /// <returns>
    /// A GUID with the requested timestamp, version, and variant bits.
    /// </returns>
    /// <remarks>
    /// This helper constructs UUID fields explicitly rather than
    /// relying on Guid.CreateVersion7, which uses the current clock.
    ///
    /// This permits tests of exact millisecond boundaries and
    /// malformed UUID version or variant fields.
    ///
    /// The remaining random fields use fixed hexadecimal digits.
    /// Cryptographic randomness is not relevant to validation tests.
    /// </remarks>
    private static Guid CreateUuid(
        DateTimeOffset timestamp,
        char version = '7',
        char variant = '8')
    {
        long unixMilliseconds = timestamp.ToUnixTimeMilliseconds();

        string timestampHex = unixMilliseconds.ToString(
            "x12",
            CultureInfo.InvariantCulture);

        string value =
            timestampHex[..8] + "-" +
            timestampHex[8..] + "-" +
            version + "123-" +
            variant + "123-" +
            "123456789abc";

        return Guid.Parse(value);
    }

    /// <summary>
    /// Creates a validator using a fixed UTC reference instant.
    /// </summary>
    /// <param name="now">Clock instant used for validation.</param>
    /// <returns>A validator with deterministic time behavior.</returns>
    private static TelemetryBatchIdValidator CreateValidator(
        DateTimeOffset? now = null)
    {
        return new TelemetryBatchIdValidator(
            new FixedTimeProvider(now ?? FixedNow));
    }

    /// <summary>
    /// Verifies the stable error code returned for a rejected
    /// new batch identifier.
    /// </summary>
    /// <param name="validator">Validator under test.</param>
    /// <param name="batchId">Identifier expected to be rejected.</param>
    /// <param name="expectedCode">Expected application error code.</param>
    private static void AssertRejected(
        TelemetryBatchIdValidator validator,
        Guid batchId,
        string expectedCode)
    {
        TelemetryBatchIdValidationException exception =
            Assert.Throws<TelemetryBatchIdValidationException>(
                () => validator.ValidateNewBatch(batchId));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal("batchId", exception.ParamName);
    }

    [Fact]
    public void Constructor_NullTimeProvider_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new TelemetryBatchIdValidator(null!));
    }

    [Fact]
    public void ValidateNewBatch_ValidCurrentUuidV7_IsAccepted()
    {
        TelemetryBatchIdValidator validator = CreateValidator();

        Guid batchId = CreateUuid(FixedNow);

        validator.ValidateNewBatch(batchId);
    }

    [Theory]
    [InlineData('8')]
    [InlineData('9')]
    [InlineData('a')]
    [InlineData('b')]
    public void ValidateNewBatch_ValidRfcVariant_IsAccepted(
        char variant)
    {
        TelemetryBatchIdValidator validator = CreateValidator();

        Guid batchId = CreateUuid(
            FixedNow,
            version: '7',
            variant: variant);

        validator.ValidateNewBatch(batchId);
    }

    [Fact]
    public void ValidateNewBatch_EmptyGuid_IsRejected()
    {
        AssertRejected(
            CreateValidator(),
            Guid.Empty,
            "batch_id_invalid");
    }

    [Theory]
    [InlineData('1')]
    [InlineData('4')]
    [InlineData('6')]
    [InlineData('8')]
    public void ValidateNewBatch_WrongUuidVersion_IsRejected(
        char version)
    {
        Guid batchId = CreateUuid(
            FixedNow,
            version: version);

        AssertRejected(
            CreateValidator(),
            batchId,
            "batch_id_invalid");
    }

    [Theory]
    [InlineData('0')]
    [InlineData('4')]
    [InlineData('7')]
    [InlineData('c')]
    [InlineData('d')]
    [InlineData('f')]
    public void ValidateNewBatch_InvalidUuidVariant_IsRejected(
        char variant)
    {
        Guid batchId = CreateUuid(
            FixedNow,
            variant: variant);

        AssertRejected(
            CreateValidator(),
            batchId,
            "batch_id_invalid");
    }

    [Fact]
    public void ValidateNewBatch_ExactlyFiveMinutesOld_IsAccepted()
    {
        TelemetryBatchIdValidator validator = CreateValidator();

        Guid batchId = CreateUuid(
            FixedNow.AddMinutes(-5));

        // The architecture rejects timestamps more than five
        // minutes old, not timestamps exactly on the boundary.
        validator.ValidateNewBatch(batchId);
    }

    [Fact]
    public void ValidateNewBatch_OneMillisecondBeyondPastBoundary_IsExpired()
    {
        Guid batchId = CreateUuid(
            FixedNow.AddMinutes(-5).AddMilliseconds(-1));

        AssertRejected(
            CreateValidator(),
            batchId,
            "batch_id_expired");
    }

    [Fact]
    public void ValidateNewBatch_ExactlyFiveMinutesInFuture_IsAccepted()
    {
        TelemetryBatchIdValidator validator = CreateValidator();

        Guid batchId = CreateUuid(
            FixedNow.AddMinutes(5));

        validator.ValidateNewBatch(batchId);
    }

    [Fact]
    public void ValidateNewBatch_OneMillisecondBeyondFutureBoundary_IsRejected()
    {
        Guid batchId = CreateUuid(
            FixedNow.AddMinutes(5).AddMilliseconds(1));

        AssertRejected(
            CreateValidator(),
            batchId,
            "batch_id_future");
    }

    [Theory]
    [InlineData(-299999)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(299999)]
    public void ValidateNewBatch_InsidePermittedWindow_IsAccepted(
        int offsetMilliseconds)
    {
        TelemetryBatchIdValidator validator = CreateValidator();

        Guid batchId = CreateUuid(
            FixedNow.AddMilliseconds(offsetMilliseconds));

        validator.ValidateNewBatch(batchId);
    }

    [Fact]
    public void ValidateNewBatch_OlderUuid_IsExpired()
    {
        Guid batchId = CreateUuid(
            FixedNow.AddDays(-1));

        AssertRejected(
            CreateValidator(),
            batchId,
            "batch_id_expired");
    }

    [Fact]
    public void ValidateNewBatch_FarFutureUuid_IsRejected()
    {
        Guid batchId = CreateUuid(
            FixedNow.AddDays(1));

        AssertRejected(
            CreateValidator(),
            batchId,
            "batch_id_future");
    }

    [Fact]
    public void ValidateNewBatch_UuidV4WithRecentLeadingBits_IsRejected()
    {
        // A recent-looking timestamp is not sufficient:
        // the UUID must actually use version 7.
        Guid batchId = CreateUuid(
            FixedNow,
            version: '4');

        AssertRejected(
            CreateValidator(),
            batchId,
            "batch_id_invalid");
    }

    [Fact]
    public void ValidateNewBatch_SameUuidAgainstLaterClock_Expires()
    {
        Guid batchId = CreateUuid(FixedNow);

        TelemetryBatchIdValidator currentValidator =
            CreateValidator(FixedNow);

        TelemetryBatchIdValidator laterValidator =
            CreateValidator(FixedNow.AddMinutes(5)
                .AddMilliseconds(1));

        // The same UUID is valid initially but expires when
        // evaluated as a new request outside its freshness window.
        //
        // An existing committed receipt would bypass this
        // validator during receipt-first replay processing.
        currentValidator.ValidateNewBatch(batchId);

        AssertRejected(
            laterValidator,
            batchId,
            "batch_id_expired");
    }

    [Fact]
    public void ValidateNewBatch_SameUuidAgainstEarlierClock_IsFuture()
    {
        Guid batchId = CreateUuid(FixedNow);

        TelemetryBatchIdValidator earlierValidator =
            CreateValidator(FixedNow.AddMinutes(-5)
                .AddMilliseconds(-1));

        AssertRejected(
            earlierValidator,
            batchId,
            "batch_id_future");
    }

    [Fact]
    public void ValidateNewBatch_MaximumUuidTimestamp_IsRejectedAsFuture()
    {
        // UUIDv7 uses a 48-bit Unix millisecond timestamp.
        // Its maximum value exceeds DateTimeOffset's range.
        //
        // The validator must safely classify this value
        // without first converting it into DateTimeOffset.
        Guid batchId = Guid.Parse(
            "ffffffff-ffff-7123-8123-123456789abc");

        AssertRejected(
            CreateValidator(),
            batchId,
            "batch_id_future");
    }

    [Fact]
    public void ValidateNewBatch_RepeatedEvaluation_IsDeterministic()
    {
        TelemetryBatchIdValidator validator = CreateValidator();
        Guid batchId = CreateUuid(FixedNow);

        validator.ValidateNewBatch(batchId);
        validator.ValidateNewBatch(batchId);
    }
}
