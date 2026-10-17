
using System;

namespace HealthMonitor.Domain.Device;

/// <summary>
/// Represents a monitored infrastructure device or service and
/// manages its inventory identity, metadata, and retirement lifecycle.
/// </summary>
/// <remarks>
/// This entity belongs to the Device bounded context.
///
/// Device identity is immutable after creation. Asset tags and site
/// codes are normalized to uppercase. Display names preserve case after trimming.
/// Device types must match the approved case-sensitive vocabulary after trimming.
///
/// Retirement is a soft operation that preserves the device record
/// for historical telemetry and incident references.
///
/// Device health, telemetry measurements, and incident state are
/// owned by the Telemetry bounded context and are not stored here.
/// </remarks>
public sealed class MonitoredDevice
{
    /// <summary>
    /// Gets the immutable identifier of the device.
    /// </summary>
    public Guid Id { get; private set; }

    /// <summary>
    /// Gets the unique, uppercase asset tag.
    /// </summary>
    /// <remarks>
    /// The maximum length is 64 characters. Database uniqueness
    /// is enforced by the Device persistence configuration.
    /// </remarks>
    public string AssetTag { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the human-readable device name.
    /// </summary>
    /// <remarks>
    /// Leading and trailing whitespace is removed during creation.
    /// The maximum length is 120 characters.
    /// </remarks>
    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the device category.
    /// </summary>
    /// <remarks>
    /// After trimming, the value must be Server, Database, Gateway, or SCADA.
    /// Case variants are rejected rather than silently normalized.
    /// </remarks>
    public string DeviceType { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the uppercase site identifier associated with the device.
    /// </summary>
    /// <remarks>
    /// The value is required and limited to 32 characters.
    /// </remarks>
    public string SiteCode { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the UTC timestamp at which the device was registered.
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>
    /// Gets the UTC retirement timestamp, or null if the device is active.
    /// </summary>
    public DateTimeOffset? RetiredAtUtc { get; private set; }

    // Required by EF Core for entity materialization.
    // Application code must use a validated public constructor.
    private MonitoredDevice() { }

    /// <summary>
    /// Creates a device using the current UTC timestamp.
    /// </summary>
    /// <param name="id">Non-empty device identifier.</param>
    /// <param name="assetTag">Required asset tag, maximum 64 characters.</param>
    /// <param name="displayName">Required display name, maximum 120 characters.</param>
    /// <param name="deviceType">Canonical Server, Database, Gateway, or SCADA category; surrounding whitespace is trimmed.</param>
    /// <param name="siteCode">Required site identifier, maximum 32 characters.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when the identifier is empty or a required string
    /// is invalid, uses an unsupported device type, or exceeds its maximum length after normalization.
    /// </exception>
    /// <remarks>
    /// Uses the same canonical category validation as the explicit-timestamp overload.
    /// Callers that need a consistent application clock should supply the timestamp explicitly.
    /// </remarks>
    public MonitoredDevice(
        Guid id,
        string assetTag,
        string displayName,
        string deviceType,
        string siteCode)
        : this(
            id,
            assetTag,
            displayName,
            deviceType,
            siteCode,
            DateTimeOffset.UtcNow)
    {
    }

    /// <summary>
    /// Creates a device with an explicitly supplied UTC timestamp.
    /// </summary>
    /// <param name="id">Non-empty device identifier.</param>
    /// <param name="assetTag">Required asset tag, maximum 64 characters.</param>
    /// <param name="displayName">Required display name, maximum 120 characters.</param>
    /// <param name="deviceType">Canonical Server, Database, Gateway, or SCADA category; surrounding whitespace is trimmed.</param>
    /// <param name="siteCode">Required site identifier, maximum 32 characters.</param>
    /// <param name="createdAtUtc">
    /// Non-default creation timestamp with a zero UTC offset.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when the identifier, required fields, or creation
    /// timestamp violates the entity's validation rules.
    /// </exception>
    /// <remarks>
    /// Accepting an explicit timestamp enables deterministic tests
    /// and allows application services to supply a consistent clock.
    ///
    /// All inputs are validated and normalized before any entity
    /// properties are assigned.
    /// </remarks>
    public MonitoredDevice(
        Guid id,
        string assetTag,
        string displayName,
        string deviceType,
        string siteCode,
        DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException(
                "Device ID cannot be empty.",
                nameof(id));
        }

        // Normalize all input values before changing entity state.
        string normalizedAssetTag =
            NormalizeRequired(assetTag, 64, true, nameof(assetTag));

        string normalizedDisplayName =
            NormalizeRequired(displayName, 120, false, nameof(displayName));

        string normalizedDeviceType =
            NormalizeRequired(deviceType, 32, false, nameof(deviceType));

        // Keep inventory categories consistent with the fixed catalog and API vocabulary.
        // This constructor does not constrain direct SQL writes or EF materialization.
        if (normalizedDeviceType is not ("Server" or "Database" or "Gateway" or "SCADA"))
        {
            throw new ArgumentException(
                "Device type must be a canonical Server, Database, Gateway, or SCADA category.",
                nameof(deviceType));
        }

        string normalizedSiteCode =
            NormalizeRequired(siteCode, 32, true, nameof(siteCode));

        ValidateUtc(createdAtUtc, nameof(createdAtUtc));

        // Assign properties only after all validation succeeds.
        Id = id;
        AssetTag = normalizedAssetTag;
        DisplayName = normalizedDisplayName;
        DeviceType = normalizedDeviceType;
        SiteCode = normalizedSiteCode;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>
    /// Soft-retires the device using the current UTC timestamp.
    /// </summary>
    /// <remarks>
    /// The operation is idempotent. If the device is already retired,
    /// its original retirement timestamp is preserved.
    /// </remarks>
    public void Retire()
    {
        if (RetiredAtUtc.HasValue)
            return;

        Retire(DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Soft-retires the device at the specified UTC timestamp.
    /// </summary>
    /// <param name="retiredAtUtc">
    /// Non-default UTC timestamp that must not precede creation.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when an active device receives an invalid retirement
    /// timestamp or a timestamp earlier than its creation.
    /// </exception>
    /// <remarks>
    /// If the device is already retired, this operation returns
    /// without validating the supplied timestamp or changing state.
    ///
    /// Retirement does not delete the device or its historical data.
    /// </remarks>
    public void Retire(DateTimeOffset retiredAtUtc)
    {
        if (RetiredAtUtc.HasValue)
            return;

        ValidateUtc(retiredAtUtc, nameof(retiredAtUtc));

        if (retiredAtUtc < CreatedAtUtc)
        {
            throw new ArgumentException(
                "Retirement cannot precede device creation.",
                nameof(retiredAtUtc));
        }

        RetiredAtUtc = retiredAtUtc;
    }

    /// <summary>
    /// Validates and normalizes a required inventory string.
    /// </summary>
    /// <param name="value">Value to validate.</param>
    /// <param name="maxLength">Maximum permitted normalized length.</param>
    /// <param name="uppercase">
    /// Whether to convert the normalized value to uppercase.
    /// </param>
    /// <param name="parameterName">
    /// Parameter name used when reporting validation errors.
    /// </param>
    /// <returns>The trimmed and optionally uppercased value.</returns>
    /// <exception cref="ArgumentException">The value is blank or exceeds the normalized length limit.</exception>
    /// <remarks>
    /// Length validation is performed after trimming and case
    /// normalization. Internal whitespace is preserved.
    /// </remarks>
    private static string NormalizeRequired(
        string value,
        int maxLength,
        bool uppercase,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "A non-empty value is required.",
                parameterName);
        }

        string normalized = value.Trim();

        if (uppercase)
            normalized = normalized.ToUpperInvariant();

        if (normalized.Length > maxLength)
        {
            throw new ArgumentException(
                $"Value cannot exceed {maxLength} characters.",
                parameterName);
        }

        return normalized;
    }

    /// <summary>
    /// Validates that a timestamp is non-default and has a zero UTC offset.
    /// </summary>
    /// <param name="value">Timestamp to validate.</param>
    /// <param name="parameterName">
    /// Parameter name used when reporting validation errors.
    /// </param>
    /// <exception cref="ArgumentException">The timestamp is default or has a nonzero offset.</exception>
    /// <remarks>
    /// A zero offset is required for consistent PostgreSQL
    /// timestamptz persistence and UTC domain semantics.
    /// </remarks>
    private static void ValidateUtc(
        DateTimeOffset value,
        string parameterName)
    {
        if (value == default ||
            value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "A valid UTC timestamp is required.",
                parameterName);
        }
    }
}
