
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HealthMonitor.Application.Telemetry;

/// <summary>
/// Validates the shape of synthetic telemetry ingestion requests
/// and computes deterministic hashes for replay detection.
/// </summary>
/// <remarks>
/// The canonical request identity is based on the simulation
/// scenario and the set of selected device identifiers.
///
/// Device identifiers are sorted before hashing so changes
/// in request ordering do not create different logical requests.
///
/// BatchId is deliberately excluded from the hash because it
/// identifies the receipt used to look up the request. Its
/// associated hash identifies the operation's content.
///
/// Server-generated measurements, observation timestamps, and
/// database state are also excluded because they are not
/// caller-controlled inputs to the logical simulation request.
///
/// This component performs no database operations.
/// </remarks>
public static class TelemetryRequestCanonicalizer
{
    private const int MinimumDeviceCount = 1;
    private const int MaximumDeviceCount = 25;

    // This identifier distinguishes the current canonical encoding
    // from any future format.
    //
    // A canonical encoding must not be changed casually because
    // previously committed receipts retain hashes generated using
    // the original encoding.
    private const string CanonicalFormatVersion = "healthmonitor-simulation-v1";

    /// <summary>
    /// Validates request shape and calculates its canonical
    /// SHA-256 request hash.
    /// </summary>
    /// <param name="request">Simulation request to canonicalize.</param>
    /// <returns>
    /// A 64-character lowercase hexadecimal SHA-256 hash.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when the request or its device collection is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown for an empty batch ID, unsupported scenario,
    /// invalid device count, empty device identifier,
    /// or duplicate device identifier.
    /// </exception>
    /// <remarks>
    /// Validation is intentionally limited to request shape.
    ///
    /// Current device catalog eligibility and UUIDv7 freshness
    /// must not be checked here. The ingestion service must first
    /// look for an existing receipt so valid historical requests
    /// remain replayable after device retirement.
    ///
    /// Scenario values must match the defined canonical tokens
    /// exactly. The method does not silently trim or change case.
    ///
    /// Request ordering does not affect the hash, but duplicate
    /// device identifiers are rejected rather than removed.
    /// </remarks>
    public static string ComputeHash(
        TelemetryIngestionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        Validate(request);

        // Sort normalized GUID representations using ordinal
        // comparison. A simulation operates on a set of devices,
        // so changing their input order must not change the
        // logical request identity.
        string[] sortedDeviceIds = request.DeviceIds
            .Select(id => id.ToString("N"))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        // Use a versioned and unambiguous canonical encoding.
        //
        // Newline delimiters are safe because the scenario is
        // restricted to fixed tokens and GUIDs contain only
        // hexadecimal characters.
        //
        // Use '\n' explicitly rather than Environment.NewLine
        // so Windows and Linux generate identical hashes.
        string canonical = string.Join(
            "\n",
            new[]
            {
                CanonicalFormatVersion,
                request.Scenario,
                sortedDeviceIds.Length.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
            }.Concat(sortedDeviceIds));

        byte[] content = Encoding.UTF8.GetBytes(canonical);
        byte[] digest = SHA256.HashData(content);

        // SimulationBatch requires a lowercase hexadecimal
        // request hash containing exactly 64 characters.
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>
    /// Validates the structural and semantic shape of a
    /// simulation request without querying external state.
    /// </summary>
    /// <param name="request">Request to validate.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when the request or its device collection is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a required identifier or scenario is invalid,
    /// the device count is outside 1–25, or IDs are duplicated.
    /// </exception>
    /// <remarks>
    /// Supported scenarios are Healthy, HighLatency, HighCpu,
    /// and Timeout.
    ///
    /// Device IDs must be distinct. Rejecting duplicates rather
    /// than silently removing them prevents a malformed request
    /// from being accepted with different semantics than the
    /// caller submitted.
    ///
    /// Validating BatchId here only rejects Guid.Empty.
    /// UUIDv7 version and timestamp checks are performed later
    /// for requests without an existing receipt.
    /// </remarks>
    public static void Validate(
        TelemetryIngestionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.BatchId == Guid.Empty)
        {
            throw new ArgumentException(
                "BatchId must not be empty.",
                nameof(request));
        }

        // Accept only the explicit scenario contract.
        //
        // Silent case conversion or whitespace trimming could
        // hide malformed input and make replay identity rules
        // harder to reason about.
        if (request.Scenario is not (
            "Healthy" or
            "HighLatency" or
            "HighCpu" or
            "Timeout"))
        {
            throw new ArgumentException(
                "Scenario must be Healthy, HighLatency, " +
                "HighCpu, or Timeout.",
                nameof(request));
        }

        IReadOnlyList<Guid> deviceIds =
            request.DeviceIds
            ?? throw new ArgumentNullException(
                nameof(request),
                "DeviceIds must not be null.");

        if (deviceIds.Count < MinimumDeviceCount ||
            deviceIds.Count > MaximumDeviceCount)
        {
            throw new ArgumentException(
                "A simulation request must contain " +
                "between 1 and 25 device IDs.",
                nameof(request));
        }

        if (deviceIds.Any(id => id == Guid.Empty))
        {
            throw new ArgumentException(
                "Device IDs must not be empty.",
                nameof(request));
        }

        // Duplicate IDs represent invalid input, not additional
        // observations. A device may appear only once per batch.
        //
        // This also matches the database uniqueness constraint
        // on BatchId and DeviceId for telemetry observations.
        if (deviceIds.Distinct().Count() != deviceIds.Count)
        {
            throw new ArgumentException(
                "Device IDs must be distinct.",
                nameof(request));
        }
    }
}
