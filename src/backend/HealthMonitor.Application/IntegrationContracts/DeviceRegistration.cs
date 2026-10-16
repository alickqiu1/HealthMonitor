namespace HealthMonitor.Application.IntegrationContracts;

/// <summary>Immutable inventory projection shared across application module boundaries.</summary>
/// <param name="DeviceId">Stable device identity; it does not itself establish eligibility.</param>
/// <param name="DisplayName">Device display name, preserving display case.</param>
/// <param name="AssetTag">Canonical inventory asset tag.</param>
/// <param name="SiteCode">Canonical site code.</param>
/// <param name="DeviceType">Inventory device category.</param>
/// <param name="IsRetired">Whether the device has been soft-retired.</param>
/// <remarks>Contains no measurements, computed health, or persistence objects.</remarks>
public sealed record DeviceRegistration(
    Guid DeviceId,
    string DisplayName,
    string AssetTag,
    string SiteCode,
    string DeviceType,
    bool IsRetired);
