
using System;
using HealthMonitor.Domain.Telemetry;
using Xunit;

namespace HealthMonitor.UnitTests.Telemetry;

/// <summary>
/// Verifies the construction and validation invariants
/// of the SimulationBatch entity.
/// </summary>
/// <remarks>
/// Tests cover committed simulation receipt creation,
/// batch identity, request hash validation and normalization,
/// canonical scenario validation, UTC timestamps, commit
/// chronology, and affected-device count boundaries.
///
/// These tests verify the domain representation of a committed
/// receipt. They do not test request replay decisions, concurrent
/// submissions, database uniqueness, or transaction atomicity.
///
/// Those responsibilities belong to application services and
/// PostgreSQL integration tests.
///
/// These are isolated domain unit tests requiring no EF Core,
/// PostgreSQL, Supabase, or external services.
/// </remarks>
public sealed class SimulationBatchTests
{
    // Fixed UTC timestamps keep chronology tests deterministic
    // and independent of the system clock.
    private static readonly DateTimeOffset RequestedAt =
        new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset CommittedAt =
        RequestedAt.AddSeconds(5);

    // Represents a valid 64-character hexadecimal request hash.
    // These tests validate hash representation, not SHA-256 computation.
    private const string ValidHash =
        "0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef";

    /// <summary>
    /// Creates a committed simulation receipt using valid defaults.
    /// </summary>
    /// <param name="batchId">
    /// Optional batch identifier. Null generates a new GUID;
    /// Guid.Empty is forwarded unchanged for domain validation.
    /// </param>
    /// <param name="requestHash">
    /// Request hash to validate. Defaults to a valid
    /// 64-character hexadecimal string.
    /// </param>
    /// <param name="scenario">
    /// Scenario identifier to validate. Defaults to a canonical value.
    /// </param>
    /// <param name="requestedAtUtc">
    /// Optional request timestamp, defaulting to RequestedAt.
    /// </param>
    /// <param name="committedAtUtc">
    /// Optional commit timestamp, defaulting to CommittedAt.
    /// </param>
    /// <param name="affectedDeviceCount">
    /// Number of affected devices, defaulting to 3.
    /// </param>
    /// <returns>A validated committed simulation receipt.</returns>
    /// <remarks>
    /// Individual tests override only the inputs relevant
    /// to the invariant being tested.
    ///
    /// The helper does not perform normalization or validation;
    /// those responsibilities remain with SimulationBatch.
    ///
    /// An explicitly supplied Guid.Empty is not replaced.
    ///
    /// Nullable timestamp parameters use null to select valid
    /// defaults. Tests for default(DateTimeOffset) therefore
    /// call SimulationBatch.CreateCommitted directly.
    /// </remarks>
    private static SimulationBatch CreateBatch(
        Guid? batchId = null,
        string requestHash = ValidHash,
        string scenario = "normal",
        DateTimeOffset? requestedAtUtc = null,
        DateTimeOffset? committedAtUtc = null,
        int affectedDeviceCount = 3)
    {
        return SimulationBatch.CreateCommitted(
            batchId ?? Guid.NewGuid(),
            requestHash,
            scenario,
            requestedAtUtc ?? RequestedAt,
            committedAtUtc ?? CommittedAt,
            affectedDeviceCount);
    }

    [Fact]
    public void CreateCommitted_ValidValues_CreatesReceipt()
    {
        Guid batchId = Guid.NewGuid();

        SimulationBatch batch = CreateBatch(
            batchId: batchId,
            affectedDeviceCount: 24);

        Assert.Equal(batchId, batch.BatchId);
        Assert.Equal(ValidHash, batch.RequestHash);
        Assert.Equal("normal", batch.Scenario);
        Assert.Equal(RequestedAt, batch.RequestedAtUtc);
        Assert.Equal(CommittedAt, batch.CommittedAtUtc);
        Assert.Equal(24, batch.AffectedDeviceCount);
    }

    [Fact]
    public void CreateCommitted_EmptyBatchId_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => CreateBatch(batchId: Guid.Empty));
    }

    [Fact]
    public void CreateCommitted_UppercaseHash_NormalizesToLowercase()
    {
        SimulationBatch batch = CreateBatch(
            requestHash: ValidHash.ToUpperInvariant());

        Assert.Equal(ValidHash, batch.RequestHash);
    }

    [Fact]
    public void CreateCommitted_MixedCaseHash_NormalizesToLowercase()
    {
        string mixedCaseHash =
            "0123456789ABCDEF0123456789abcdef" +
            "0123456789ABCDEF0123456789abcdef";

        SimulationBatch batch = CreateBatch(
            requestHash: mixedCaseHash);

        Assert.Equal(ValidHash, batch.RequestHash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateCommitted_MissingHash_Throws(string? hash)
    {
        Assert.Throws<ArgumentException>(
            () => CreateBatch(requestHash: hash!));
    }

    [Theory]
    [InlineData(63)]
    [InlineData(65)]
    [InlineData(0)]
    [InlineData(1)]
    public void CreateCommitted_InvalidHashLength_Throws(int length)
    {
        string hash = new('a', length);

        Assert.Throws<ArgumentException>(
            () => CreateBatch(requestHash: hash));
    }

    [Fact]
    public void CreateCommitted_Exactly64HexCharacters_IsAccepted()
    {
        SimulationBatch batch = CreateBatch(
            requestHash: new string('f', 64));

        Assert.Equal(new string('f', 64), batch.RequestHash);
    }

    [Theory]
    [InlineData('g')]
    [InlineData('z')]
    [InlineData('G')]
    [InlineData(' ')]
    [InlineData('-')]
    public void CreateCommitted_NonHexCharacter_Throws(char invalidCharacter)
    {
        string hash = new string('a', 63) + invalidCharacter;

        Assert.Throws<ArgumentException>(
            () => CreateBatch(requestHash: hash));
    }

    [Fact]
    public void CreateCommitted_HashWithSurroundingWhitespace_Throws()
    {
        string hash = " " + ValidHash + " ";

        // The hash is case-normalized but deliberately not trimmed.
        Assert.Throws<ArgumentException>(
            () => CreateBatch(requestHash: hash));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateCommitted_MissingScenario_Throws(string? scenario)
    {
        Assert.Throws<ArgumentException>(
            () => CreateBatch(scenario: scenario!));
    }

    [Theory]
    [InlineData(" normal")]
    [InlineData("normal ")]
    [InlineData(" normal ")]
    [InlineData("\tnormal")]
    public void CreateCommitted_NonCanonicalScenario_Throws(string scenario)
    {
        Assert.Throws<ArgumentException>(
            () => CreateBatch(scenario: scenario));
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("latency")]
    [InlineData("cpu")]
    [InlineData("connectivity")]
    public void CreateCommitted_TrimmedScenario_IsAccepted(string scenario)
    {
        SimulationBatch batch = CreateBatch(
            scenario: scenario);

        Assert.Equal(scenario, batch.Scenario);
    }

    [Fact]
    public void CreateCommitted_ScenarioCase_IsPreserved()
    {
        SimulationBatch batch = CreateBatch(
            scenario: "Normal");

        // The entity enforces trimming, not scenario case
        // normalization or membership in a scenario allowlist.
        Assert.Equal("Normal", batch.Scenario);
    }

    [Fact]
    public void CreateCommitted_DefaultRequestTimestamp_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => SimulationBatch.CreateCommitted(
                Guid.NewGuid(),
                ValidHash,
                "normal",
                default(DateTimeOffset),
                CommittedAt,
                3));
    }

    [Fact]
    public void CreateCommitted_DefaultCommitTimestamp_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => SimulationBatch.CreateCommitted(
                Guid.NewGuid(),
                ValidHash,
                "normal",
                RequestedAt,
                default(DateTimeOffset),
                3));
    }

    [Fact]
    public void CreateCommitted_NonUtcRequestTimestamp_Throws()
    {
        DateTimeOffset nonUtc =
            new(2026, 10, 14, 12, 0, 0, TimeSpan.FromHours(-6));

        Assert.Throws<ArgumentException>(
            () => CreateBatch(requestedAtUtc: nonUtc));
    }

    [Fact]
    public void CreateCommitted_NonUtcCommitTimestamp_Throws()
    {
        DateTimeOffset nonUtc =
            new(2026, 10, 14, 12, 0, 0, TimeSpan.FromHours(-6));

        Assert.Throws<ArgumentException>(
            () => CreateBatch(committedAtUtc: nonUtc));
    }

    [Fact]
    public void CreateCommitted_CommitAfterRequest_IsAccepted()
    {
        SimulationBatch batch = CreateBatch();

        Assert.True(batch.CommittedAtUtc > batch.RequestedAtUtc);
    }

    [Fact]
    public void CreateCommitted_CommitAtRequestTime_IsAccepted()
    {
        SimulationBatch batch = CreateBatch(
            committedAtUtc: RequestedAt);

        Assert.Equal(RequestedAt, batch.CommittedAtUtc);
    }

    [Fact]
    public void CreateCommitted_CommitBeforeRequest_Throws()
    {
        DateTimeOffset earlierCommit =
            RequestedAt.AddTicks(-1);

        Assert.Throws<ArgumentException>(
            () => CreateBatch(committedAtUtc: earlierCommit));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(24)]
    [InlineData(int.MaxValue)]
    public void CreateCommitted_NonNegativeAffectedCount_IsAccepted(
        int affectedDeviceCount)
    {
        SimulationBatch batch = CreateBatch(
            affectedDeviceCount: affectedDeviceCount);

        Assert.Equal(affectedDeviceCount, batch.AffectedDeviceCount);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void CreateCommitted_NegativeAffectedCount_Throws(
        int affectedDeviceCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateBatch(
                affectedDeviceCount: affectedDeviceCount));
    }

    [Fact]
    public void CreateCommitted_SeparateCalls_CreateIndependentReceipts()
    {
        SimulationBatch first = CreateBatch();
        SimulationBatch second = CreateBatch();

        Assert.NotEqual(first.BatchId, second.BatchId);
    }

    [Fact]
    public void CreateCommitted_SuppliedBatchId_IsPreserved()
    {
        Guid batchId = Guid.NewGuid();

        SimulationBatch batch = CreateBatch(batchId: batchId);

        Assert.Equal(batchId, batch.BatchId);
    }

    /// <summary>
    /// Verifies that receipt creation does not independently
    /// enforce global batch-ID uniqueness.
    /// </summary>
    /// <remarks>
    /// The domain factory validates the batch ID format but does
    /// not query existing receipts.
    ///
    /// Database uniqueness and application-level replay handling
    /// are responsible for preventing conflicting persistence.
    /// </remarks>
    [Fact]
    public void CreateCommitted_SameBatchId_AllowsIndependentDomainObjects()
    {
        Guid batchId = Guid.NewGuid();

        SimulationBatch first = CreateBatch(
            batchId: batchId);

        SimulationBatch second = CreateBatch(
            batchId: batchId,
            requestHash: new string('b', 64));

        Assert.Equal(batchId, first.BatchId);
        Assert.Equal(batchId, second.BatchId);
        Assert.NotSame(first, second);
        Assert.NotEqual(first.RequestHash, second.RequestHash);
    }

}
