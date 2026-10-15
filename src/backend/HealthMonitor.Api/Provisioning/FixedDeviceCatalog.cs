
namespace HealthMonitor.Api.Provisioning;

/// <summary>
/// Defines the immutable identity and inventory metadata for a
/// fictional device used during initial catalog provisioning.
/// </summary>
/// <remarks>
/// These definitions are provisioning inputs, not persisted domain
/// entities. The provisioning process creates MonitoredDevice records
/// from them without exposing inventory writes through the public API.
/// </remarks>
/// <param name="Id">Stable, non-empty device identifier.</param>
/// <param name="AssetTag">Unique uppercase asset tag.</param>
/// <param name="DisplayName">Human-readable device name.</param>
/// <param name="DeviceType">Approved demonstration device category.</param>
/// <param name="SiteCode">Uppercase fictional site identifier.</param>
internal sealed record DeviceSeedDefinition(
    Guid Id,
    string AssetTag,
    string DisplayName,
    string DeviceType,
    string SiteCode);

/// <summary>
/// Provides the fixed fictional device inventory for the Health Monitor demo.
/// </summary>
/// <remarks>
/// The catalog contains 24 devices distributed evenly across three
/// demonstration sites: Edmonton (EDM), Calgary (CGY), and Denver (DEN).
///
/// Each site contains two devices in each of four categories:
/// Server, Database, Gateway, and SCADA.
///
/// The catalog is deterministic so repeated provisioning uses the same
/// device identities and metadata. It must not contain real customer
/// information, production asset identifiers, or network addresses.
///
/// Once provisioned, device IDs and asset tags must remain stable.
/// Changes to existing definitions require an explicit data migration
/// or a reviewed inventory-management operation.
///
/// This class defines inventory only. Device health, telemetry,
/// incidents, and failure scenarios belong to the Telemetry module.
/// </remarks>
internal static class FixedDeviceCatalog
{
    /// <summary>
    /// Gets the fixed set of device definitions used for provisioning.
    /// </summary>
    /// <remarks>
    /// Definitions are grouped by site for readability.
    /// The provisioning service validates uniqueness, required fields,
    /// site distribution, and existing database records before insertion.
    ///
    /// This property does not access or modify the database.
    /// </remarks>
    public static IReadOnlyList<DeviceSeedDefinition> Devices { get; } =
    [
        // Edmonton Demo Facility
        Device(1, "EDM-SRV-001", "Edmonton Application Server 1", "Server", "EDM"),
        Device(2, "EDM-SRV-002", "Edmonton Application Server 2", "Server", "EDM"),
        Device(3, "EDM-DB-001", "Edmonton Database Server 1", "Database", "EDM"),
        Device(4, "EDM-DB-002", "Edmonton Database Server 2", "Database", "EDM"),
        Device(5, "EDM-GW-001", "Edmonton Network Gateway 1", "Gateway", "EDM"),
        Device(6, "EDM-GW-002", "Edmonton Network Gateway 2", "Gateway", "EDM"),
        Device(7, "EDM-SCADA-001", "Edmonton SCADA Controller 1", "SCADA", "EDM"),
        Device(8, "EDM-SCADA-002", "Edmonton SCADA Controller 2", "SCADA", "EDM"),

        // Calgary Demo Facility
        Device(9, "CGY-SRV-001", "Calgary Application Server 1", "Server", "CGY"),
        Device(10, "CGY-SRV-002", "Calgary Application Server 2", "Server", "CGY"),
        Device(11, "CGY-DB-001", "Calgary Database Server 1", "Database", "CGY"),
        Device(12, "CGY-DB-002", "Calgary Database Server 2", "Database", "CGY"),
        Device(13, "CGY-GW-001", "Calgary Network Gateway 1", "Gateway", "CGY"),
        Device(14, "CGY-GW-002", "Calgary Network Gateway 2", "Gateway", "CGY"),
        Device(15, "CGY-SCADA-001", "Calgary SCADA Controller 1", "SCADA", "CGY"),
        Device(16, "CGY-SCADA-002", "Calgary SCADA Controller 2", "SCADA", "CGY"),

        // Denver Demo Facility
        Device(17, "DEN-SRV-001", "Denver Application Server 1", "Server", "DEN"),
        Device(18, "DEN-SRV-002", "Denver Application Server 2", "Server", "DEN"),
        Device(19, "DEN-DB-001", "Denver Database Server 1", "Database", "DEN"),
        Device(20, "DEN-DB-002", "Denver Database Server 2", "Database", "DEN"),
        Device(21, "DEN-GW-001", "Denver Network Gateway 1", "Gateway", "DEN"),
        Device(22, "DEN-GW-002", "Denver Network Gateway 2", "Gateway", "DEN"),
        Device(23, "DEN-SCADA-001", "Denver SCADA Controller 1", "SCADA", "DEN"),
        Device(24, "DEN-SCADA-002", "Denver SCADA Controller 2", "SCADA", "DEN")
    ];

    /// <summary>
    /// Creates a device definition with a deterministic demonstration UUID.
    /// </summary>
    /// <param name="number">
    /// Unique device sequence number in the fixed inventory.
    /// </param>
    /// <param name="assetTag">Canonical asset tag.</param>
    /// <param name="displayName">Human-readable device name.</param>
    /// <param name="deviceType">Device category.</param>
    /// <param name="siteCode">Fictional site identifier.</param>
    /// <returns>A fixed device definition.</returns>
    /// <remarks>
    /// The sequence number is embedded in the UUID's final 12 hexadecimal
    /// digits. This produces stable identifiers across provisioning runs.
    ///
    /// The identifiers are intentionally deterministic, not random.
    /// They must not be used as authentication or authorization secrets.
    ///
    /// Do not renumber existing entries after initial provisioning.
    /// </remarks>
    private static DeviceSeedDefinition Device(
        int number,
        string assetTag,
        string displayName,
        string deviceType,
        string siteCode)
    {
        Guid id = Guid.Parse(
            $"71e0cafe-2026-4000-8000-{number:D12}");

        return new DeviceSeedDefinition(
            id,
            assetTag,
            displayName,
            deviceType,
            siteCode);
    }
}
