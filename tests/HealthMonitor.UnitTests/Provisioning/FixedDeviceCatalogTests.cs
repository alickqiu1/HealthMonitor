
using HealthMonitor.Api.Provisioning;

namespace HealthMonitor.UnitTests.Provisioning;

/// <summary>
/// Verifies the identity, structure, and invariants of the fixed
/// fictional device inventory used for initial provisioning.
/// </summary>
public sealed class FixedDeviceCatalogTests
{
    [Fact]
    public void Catalog_ContainsExactly24Devices()
    {
        Assert.Equal(24, FixedDeviceCatalog.Devices.Count);
    }

    [Fact]
    public void Catalog_HasUniqueNonEmptyDeviceIds()
    {
        var devices = FixedDeviceCatalog.Devices;

        Assert.All(devices, device =>
            Assert.NotEqual(Guid.Empty, device.Id));

        Assert.Equal(
            devices.Count,
            devices.Select(device => device.Id).Distinct().Count());
    }

    [Fact]
    public void Catalog_HasUniqueCanonicalAssetTags()
    {
        var devices = FixedDeviceCatalog.Devices;

        Assert.All(devices, device =>
        {
            Assert.False(string.IsNullOrWhiteSpace(device.AssetTag));
            Assert.Equal(
                device.AssetTag.ToUpperInvariant(),
                device.AssetTag);
            Assert.Equal(device.AssetTag.Trim(), device.AssetTag);
            Assert.True(device.AssetTag.Length <= 64);
        });

        Assert.Equal(
            devices.Count,
            devices.Select(device => device.AssetTag)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
    }

    [Fact]
    public void Catalog_HasEightDevicesAtEachSite()
    {
        var devices = FixedDeviceCatalog.Devices;

        Assert.Equal(
            3,
            devices.Select(device => device.SiteCode).Distinct().Count());

        foreach (string site in new[] { "EDM", "CGY", "DEN" })
        {
            Assert.Equal(
                8,
                devices.Count(device => device.SiteCode == site));
        }
    }

    [Fact]
    public void Catalog_UsesApprovedDeviceTypes()
    {
        string[] allowedTypes =
        [
            "Server",
            "Database",
            "Gateway",
            "SCADA"
        ];

        Assert.All(FixedDeviceCatalog.Devices, device =>
            Assert.Contains(device.DeviceType, allowedTypes));
    }

    [Fact]
    public void Catalog_HasValidDisplayNames()
    {
        Assert.All(FixedDeviceCatalog.Devices, device =>
        {
            Assert.False(string.IsNullOrWhiteSpace(device.DisplayName));
            Assert.Equal(device.DisplayName.Trim(), device.DisplayName);
            Assert.True(device.DisplayName.Length <= 120);
        });
    }

    [Fact]
    public void Catalog_HasStableIdentifiers()
    {
        var devices = FixedDeviceCatalog.Devices;

        Assert.Equal(
            Guid.Parse("71e0cafe-2026-4000-8000-000000000001"),
            devices.Single(device =>
                device.AssetTag == "EDM-SRV-001").Id);

        Assert.Equal(
            Guid.Parse("71e0cafe-2026-4000-8000-000000000024"),
            devices.Single(device =>
                device.AssetTag == "DEN-SCADA-002").Id);
    }

    [Fact]
    public void Catalog_HasTwoDevicesOfEachTypeAtEachSite()
    {
        foreach (string site in new[] { "EDM", "CGY", "DEN" })
        {
            foreach (string type in new[]
            {
                "Server", "Database", "Gateway", "SCADA"
            })
            {
                Assert.Equal(
                    2,
                    FixedDeviceCatalog.Devices.Count(device =>
                        device.SiteCode == site &&
                        device.DeviceType == type));
            }
        }
    }
}
