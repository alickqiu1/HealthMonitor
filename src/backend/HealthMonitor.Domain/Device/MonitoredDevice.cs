
using System;

namespace HealthMonitor.Domain.Device;

/// <summary>
/// Represents a monitored infrastructure device or service.
/// </summary>
public sealed class MonitoredDevice
{
    public Guid Id { get; private set; }

    public string AssetTag { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public string DeviceType { get; private set; } = string.Empty;

    public string SiteCode { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? RetiredAtUtc { get; private set; }

    // Used by EF Core during materialization.
    private MonitoredDevice() { }

    /// <summary>
    /// Creates a device using the current UTC timestamp.
    /// Preserves compatibility with existing callers.
    /// </summary>
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
    /// Creates a device with an explicit UTC timestamp.
    /// Useful for deterministic testing and application services.
    /// </summary>
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

        // Validate and normalize all inputs before assignment.
        string normalizedAssetTag =
            NormalizeRequired(assetTag, 64, true, nameof(assetTag));

        string normalizedDisplayName =
            NormalizeRequired(displayName, 120, false, nameof(displayName));

        string normalizedDeviceType =
            NormalizeRequired(deviceType, 32, false, nameof(deviceType));

        string normalizedSiteCode =
            NormalizeRequired(siteCode, 32, true, nameof(siteCode));

        ValidateUtc(createdAtUtc, nameof(createdAtUtc));

        // Assign only after all validation has succeeded.
        Id = id;
        AssetTag = normalizedAssetTag;
        DisplayName = normalizedDisplayName;
        DeviceType = normalizedDeviceType;
        SiteCode = normalizedSiteCode;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>
    /// Soft-retires the device using the current UTC timestamp.
    /// Repeated retirement calls preserve the original timestamp.
    /// </summary>
    public void Retire()
    {
        if (RetiredAtUtc.HasValue)
            return;

        Retire(DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Soft-retires the device at the specified UTC timestamp.
    /// </summary>
    public void Retire(DateTimeOffset retiredAtUtc)
    {
        // Preserve idempotent retirement behavior.
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
    /// Validates, trims, and optionally uppercases a required value.
    /// </summary>
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
    /// Ensures a valid timestamp with a zero UTC offset.
    /// </summary>
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
