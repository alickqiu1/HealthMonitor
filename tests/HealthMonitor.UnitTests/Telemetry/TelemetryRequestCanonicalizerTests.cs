
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HealthMonitor.Application.Telemetry;
using Xunit;

namespace HealthMonitor.UnitTests.Telemetry;

/// <summary>
/// Verifies validation and deterministic hashing of synthetic
/// telemetry ingestion requests.
/// </summary>
/// <remarks>
/// Tests cover supported scenarios, device-count boundaries,
/// identifier validation, duplicate detection, canonical
/// ordering, and stable SHA-256 hashing.
///
/// The canonical request identity consists of the scenario
/// and sorted distinct device identifiers.
///
/// BatchId identifies a particular ingestion attempt but is
/// deliberately excluded from the canonical request hash.
///
/// Tests also verify that request validation does not require
/// database access, device catalog eligibility, or UUIDv7
/// timestamp checks. Those responsibilities belong to the
/// ingestion orchestration layer.
///
/// No PostgreSQL or Supabase connection is required.
/// </remarks>
public sealed class TelemetryRequestCanonicalizerTests
{
    /// <summary>
    /// Creates a deterministic, nonempty device identifier
    /// from a positive integer.
    /// </summary>
    /// <param name="value">Positive identifier sequence value.</param>
    /// <returns>A repeatable nonempty GUID.</returns>
    /// <remarks>
    /// Deterministic identifiers allow tests to compare canonical
    /// encodings without depending on randomly generated values.
    /// </remarks>
    private static Guid DeviceId(int value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return Guid.Parse(
            $"00000000-0000-0000-0000-{value:x12}");
    }

    /// <summary>
    /// Creates a simulation request with valid default values.
    /// </summary>
    /// <param name="scenario">Requested simulation scenario.</param>
    /// <param name="deviceIds">
    /// Optional device identifiers. Null uses a single valid ID.
    /// </param>
    /// <param name="batchId">
    /// Optional batch identifier. Null generates a new GUID;
    /// Guid.Empty is passed through for validation tests.
    /// </param>
    /// <returns>A request instance without implicit validation.</returns>
    /// <remarks>
    /// This helper deliberately does not force UUIDv7 because
    /// canonicalization validates request shape, not batch age
    /// or UUID version.
    /// </remarks>
    private static TelemetryIngestionRequest CreateRequest(
        string scenario = "Healthy",
        IReadOnlyList<Guid>? deviceIds = null,
        Guid? batchId = null)
    {
        return new TelemetryIngestionRequest(
            batchId ?? Guid.NewGuid(),
            scenario,
            deviceIds ?? new[] { DeviceId(1) });
    }

    [Theory]
    [InlineData("Healthy")]
    [InlineData("HighLatency")]
    [InlineData("HighCpu")]
    [InlineData("Timeout")]
    public void Validate_SupportedScenario_IsAccepted(string scenario)
    {
        TelemetryIngestionRequest request =
            CreateRequest(scenario: scenario);

        TelemetryRequestCanonicalizer.Validate(request);

        string hash =
            TelemetryRequestCanonicalizer.ComputeHash(request);

        Assert.Equal(64, hash.Length);
    }

    [Fact]
    public void Validate_OneDevice_IsAccepted()
    {
        TelemetryIngestionRequest request =
            CreateRequest(deviceIds: new[] { DeviceId(1) });

        TelemetryRequestCanonicalizer.Validate(request);

        Assert.Equal(
            64,
            TelemetryRequestCanonicalizer.ComputeHash(request).Length);
    }

    [Fact]
    public void Validate_TwentyFiveDevices_IsAccepted()
    {
        Guid[] deviceIds = Enumerable.Range(1, 25)
            .Select(DeviceId)
            .ToArray();

        TelemetryIngestionRequest request =
            CreateRequest(deviceIds: deviceIds);

        TelemetryRequestCanonicalizer.Validate(request);

        Assert.Equal(
            64,
            TelemetryRequestCanonicalizer.ComputeHash(request).Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(26)]
    [InlineData(100)]
    public void Validate_InvalidDeviceCount_Throws(int count)
    {
        Guid[] deviceIds = Enumerable.Range(1, count)
            .Select(DeviceId)
            .ToArray();

        TelemetryIngestionRequest request =
            CreateRequest(deviceIds: deviceIds);

        Assert.Throws<ArgumentException>(
            () => TelemetryRequestCanonicalizer.Validate(request));
    }

    [Theory]
    [InlineData("")]
    [InlineData("healthy")]
    [InlineData("HEALTHY")]
    [InlineData("Healthy ")]
    [InlineData(" HighCpu")]
    [InlineData("Unknown")]
    public void Validate_UnsupportedScenario_Throws(string scenario)
    {
        TelemetryIngestionRequest request =
            CreateRequest(scenario: scenario);

        Assert.Throws<ArgumentException>(
            () => TelemetryRequestCanonicalizer.Validate(request));
    }

    [Fact]
    public void Validate_NullScenario_Throws()
    {
        TelemetryIngestionRequest request =
            CreateRequest(scenario: null!);

        Assert.Throws<ArgumentException>(
            () => TelemetryRequestCanonicalizer.Validate(request));
    }

    [Fact]
    public void Validate_EmptyBatchId_Throws()
    {
        TelemetryIngestionRequest request =
            CreateRequest(batchId: Guid.Empty);

        Assert.Throws<ArgumentException>(
            () => TelemetryRequestCanonicalizer.Validate(request));
    }

    [Fact]
    public void Validate_EmptyDeviceId_Throws()
    {
        TelemetryIngestionRequest request =
            CreateRequest(
                deviceIds: new[] { DeviceId(1), Guid.Empty });

        Assert.Throws<ArgumentException>(
            () => TelemetryRequestCanonicalizer.Validate(request));
    }

    [Fact]
    public void Validate_DuplicateDeviceIds_Throws()
    {
        TelemetryIngestionRequest request =
            CreateRequest(
                deviceIds: new[]
                {
                    DeviceId(1),
                    DeviceId(2),
                    DeviceId(1)
                });

        // Duplicate IDs are invalid rather than silently removed.
        //
        // Accepting duplicates would conceal an invalid request
        // and could create ambiguity about affected-device counts.
        Assert.Throws<ArgumentException>(
            () => TelemetryRequestCanonicalizer.Validate(request));
    }

    [Fact]
    public void Validate_NullDeviceCollection_Throws()
    {
        TelemetryIngestionRequest request =
            CreateRequest(deviceIds: null);

        // The helper uses a valid default when null is passed.
        // Construct directly to test the invalid contract shape.
        request = new TelemetryIngestionRequest(
            request.BatchId,
            request.Scenario,
            null!);

        Assert.Throws<ArgumentNullException>(
            () => TelemetryRequestCanonicalizer.Validate(request));
    }

    [Fact]
    public void Validate_NullRequest_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => TelemetryRequestCanonicalizer.Validate(null!));
    }

    [Fact]
    public void ComputeHash_NullRequest_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => TelemetryRequestCanonicalizer.ComputeHash(null!));
    }

    [Fact]
    public void ComputeHash_ReturnsLowercaseSha256Hex()
    {
        TelemetryIngestionRequest request = CreateRequest();

        string hash =
            TelemetryRequestCanonicalizer.ComputeHash(request);

        Assert.Equal(64, hash.Length);

        Assert.All(hash, character =>
            Assert.True(
                character is >= '0' and <= '9'
                    or >= 'a' and <= 'f',
                $"Unexpected hash character: {character}"));
    }

    [Fact]
    public void ComputeHash_SameRequest_IsDeterministic()
    {
        TelemetryIngestionRequest request =
            CreateRequest(
                scenario: "HighLatency",
                deviceIds: new[]
                {
                    DeviceId(1),
                    DeviceId(2),
                    DeviceId(3)
                });

        string first =
            TelemetryRequestCanonicalizer.ComputeHash(request);

        string second =
            TelemetryRequestCanonicalizer.ComputeHash(request);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ComputeHash_DifferentDeviceOrdering_ProducesSameHash()
    {
        TelemetryIngestionRequest first = CreateRequest(
            scenario: "HighCpu",
            deviceIds: new[]
            {
                DeviceId(3),
                DeviceId(1),
                DeviceId(2)
            });

        TelemetryIngestionRequest second = CreateRequest(
            scenario: "HighCpu",
            deviceIds: new[]
            {
                DeviceId(2),
                DeviceId(3),
                DeviceId(1)
            },
            batchId: first.BatchId);

        // A batch targets a set of devices, not an ordered
        // sequence. Reordering the same IDs must not change
        // the replay identity of the request.
        Assert.Equal(
            TelemetryRequestCanonicalizer.ComputeHash(first),
            TelemetryRequestCanonicalizer.ComputeHash(second));
    }

    [Fact]
    public void ComputeHash_DifferentBatchIds_ProduceSameHash()
    {
        TelemetryIngestionRequest first = CreateRequest(
            batchId: Guid.NewGuid());

        TelemetryIngestionRequest second = CreateRequest(
            batchId: Guid.NewGuid());

        // The receipt is looked up by BatchId separately.
        // The hash compares request content, so BatchId must
        // not be part of that content identity.
        Assert.Equal(
            TelemetryRequestCanonicalizer.ComputeHash(first),
            TelemetryRequestCanonicalizer.ComputeHash(second));
    }

    [Fact]
    public void ComputeHash_DifferentScenario_ProducesDifferentHash()
    {
        Guid batchId = Guid.NewGuid();

        TelemetryIngestionRequest first = CreateRequest(
            scenario: "Healthy",
            batchId: batchId);

        TelemetryIngestionRequest second = CreateRequest(
            scenario: "Timeout",
            batchId: batchId);

        // Reusing a receipt identifier for a different scenario
        // must be detected as a conflicting replay.
        Assert.NotEqual(
            TelemetryRequestCanonicalizer.ComputeHash(first),
            TelemetryRequestCanonicalizer.ComputeHash(second));
    }

    [Fact]
    public void ComputeHash_DifferentDeviceSet_ProducesDifferentHash()
    {
        Guid batchId = Guid.NewGuid();

        TelemetryIngestionRequest first = CreateRequest(
            deviceIds: new[]
            {
                DeviceId(1),
                DeviceId(2)
            },
            batchId: batchId);

        TelemetryIngestionRequest second = CreateRequest(
            deviceIds: new[]
            {
                DeviceId(1),
                DeviceId(3)
            },
            batchId: batchId);

        Assert.NotEqual(
            TelemetryRequestCanonicalizer.ComputeHash(first),
            TelemetryRequestCanonicalizer.ComputeHash(second));
    }

    [Fact]
    public void ComputeHash_DifferentDeviceCount_ProducesDifferentHash()
    {
        TelemetryIngestionRequest first = CreateRequest(
            deviceIds: new[] { DeviceId(1) });

        TelemetryIngestionRequest second = CreateRequest(
            deviceIds: new[]
            {
                DeviceId(1),
                DeviceId(2)
            });

        Assert.NotEqual(
            TelemetryRequestCanonicalizer.ComputeHash(first),
            TelemetryRequestCanonicalizer.ComputeHash(second));
    }

    [Fact]
    public void ComputeHash_MatchesExplicitCanonicalEncoding()
    {
        TelemetryIngestionRequest request = CreateRequest(
            scenario: "Healthy",
            deviceIds: new[]
            {
                DeviceId(2),
                DeviceId(1)
            });

        // This fixture independently specifies the persisted
        // canonical encoding rather than reconstructing it
        // through the production canonicalizer.
        //
        // If the implementation format changes unintentionally,
        // this test fails and protects historical replay hashes.
        const string canonical =
            "healthmonitor-simulation-v1\n" +
            "Healthy\n" +
            "2\n" +
            "00000000000000000000000000000001\n" +
            "00000000000000000000000000000002";

        string expectedHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

        string actualHash =
            TelemetryRequestCanonicalizer.ComputeHash(request);

        Assert.Equal(expectedHash, actualHash);
    }

    [Fact]
    public void ComputeHash_DoesNotModifyOriginalDeviceCollection()
    {
        var deviceIds = new List<Guid>
        {
            DeviceId(3),
            DeviceId(1),
            DeviceId(2)
        };

        Guid[] originalOrder = deviceIds.ToArray();

        TelemetryIngestionRequest request =
            CreateRequest(deviceIds: deviceIds);

        TelemetryRequestCanonicalizer.ComputeHash(request);

        // Sorting must operate on a separate representation.
        // Canonicalization must not reorder the caller's list.
        Assert.Equal(originalOrder, deviceIds);
    }

    [Fact]
    public void Validate_DoesNotRequireUuidVersionSeven()
    {
        // UUIDv7 freshness applies only to new ingestion work,
        // after the initial receipt lookup.
        //
        // Shape validation must permit an existing receipt to
        // be checked even if the supplied ID is not UUIDv7.
        Guid nonVersionSevenId = Guid.Parse(
            "11111111-1111-4111-8111-111111111111");

        TelemetryIngestionRequest request =
            CreateRequest(batchId: nonVersionSevenId);

        TelemetryRequestCanonicalizer.Validate(request);

        Assert.Equal(
            64,
            TelemetryRequestCanonicalizer.ComputeHash(request).Length);
    }
}
