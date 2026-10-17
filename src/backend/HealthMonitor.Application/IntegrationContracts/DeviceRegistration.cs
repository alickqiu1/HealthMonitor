namespace HealthMonitor.Application.IntegrationContracts;

/// <summary>Immutable inventory projection shared across application module boundaries.</summary>
/// <param name="DeviceId">Stable device identity; it does not itself establish eligibility.</param>
/// <param name="DisplayName">Device display name, preserving display case.</param>
/// <param name="AssetTag">Canonical inventory asset tag.</param>
/// <param name="SiteCode">Canonical site code.</param>
/// <param name="DeviceType">Inventory device category.</param>
/// <param name="IsRetired">Whether the device has been soft-retired.</param>
/// <remarks>
/// A value snapshot avoids exposing tracked Device entities or persistence dependencies to
/// other modules. Contains no measurements or computed health, and does not validate eligibility.
/// Immutability preserves the returned facts; it does not make later catalog reads consistent
/// with this snapshot or enforce domain validation on independently constructed projections.
/// </remarks>
public sealed record DeviceRegistration(
    Guid DeviceId,
    string DisplayName,
    string AssetTag,
    string SiteCode,
    string DeviceType,
    bool IsRetired);
