
using System;
using HealthMonitor.Domain.Device;
using Xunit;

namespace HealthMonitor.UnitTests.Device;

/// <summary>
/// Verifies the domain invariants and lifecycle behavior
/// of the MonitoredDevice entity.
/// </summary>
/// <remarks>
/// Tests cover valid construction, input normalization,
/// required fields, canonical case-sensitive categories, length boundaries, UTC timestamp
/// validation, and soft retirement.
///
/// Tests also verify that invalid operations preserve entity
/// state and that repeated retirement is idempotent.
///
/// These are isolated domain unit tests. They do not require
/// EF Core, PostgreSQL, or external services.
/// </remarks>
public sealed class MonitoredDeviceTests
{
    // A fixed UTC timestamp makes construction and retirement
    // boundary tests deterministic and independent of wall-clock time.
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Creates a monitored device using valid default inventory values.
    /// </summary>
    /// <param name="id">Optional device identifier.</param>
    /// <param name="assetTag">Asset tag to validate.</param>
    /// <param name="displayName">Display name to validate.</param>
    /// <param name="deviceType">Category passed unchanged to the domain; the helper does not normalize invalid inputs.</param>
    /// <param name="siteCode">Site identifier to validate.</param>
    /// <param name="createdAt">
    /// Optional creation timestamp, defaulting to the fixed test timestamp.
    /// </param>
    /// <returns>A validated monitored device.</returns>
    /// <remarks>
    /// Tests override only the values relevant to the behavior
    /// under test, avoiding repeated setup.
    ///
    /// Normalization and validation remain the responsibility
    /// of the domain entity.
    ///
    /// To test an explicitly supplied default DateTimeOffset,
    /// call the domain constructor directly because nullable
    /// parameters use null to select the helper's default value.
    /// </remarks>
    private static MonitoredDevice CreateDevice(
        Guid? id = null,
        string assetTag = "EDM-SRV-001",
        string displayName = "Application Server",
        string deviceType = "Server",
        string siteCode = "EDM",
        DateTimeOffset? createdAt = null)
    {
        return new MonitoredDevice(
            id ?? Guid.NewGuid(),
            assetTag,
            displayName,
            deviceType,
            siteCode,
            createdAt ?? CreatedAt);
    }

    [Fact]
    public void Constructor_ValidValues_CreatesActiveDevice()
    {
        Guid id = Guid.NewGuid();

        MonitoredDevice device = CreateDevice(id: id);

        Assert.Equal(id, device.Id);
        Assert.Equal("EDM-SRV-001", device.AssetTag);
        Assert.Equal("Application Server", device.DisplayName);
        Assert.Equal("Server", device.DeviceType);
        Assert.Equal("EDM", device.SiteCode);
        Assert.Equal(CreatedAt, device.CreatedAtUtc);
        Assert.Null(device.RetiredAtUtc);
    }

    [Fact]
    public void Constructor_NormalizesInventoryValues()
    {
        MonitoredDevice device = CreateDevice(
            assetTag: "  edm-srv-001  ",
            displayName: "  Application Server  ",
            deviceType: "  Server  ",
            siteCode: "  edm  ");

        Assert.Equal("EDM-SRV-001", device.AssetTag);
        Assert.Equal("Application Server", device.DisplayName);
        Assert.Equal("Server", device.DeviceType);
        Assert.Equal("EDM", device.SiteCode);
    }

    [Fact]
    public void Constructor_EmptyId_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => CreateDevice(id: Guid.Empty));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_InvalidAssetTag_Throws(string? value)
    {
        Assert.Throws<ArgumentException>(
            () => CreateDevice(assetTag: value!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_InvalidDisplayName_Throws(string? value)
    {
        Assert.Throws<ArgumentException>(
            () => CreateDevice(displayName: value!));
    }

    /// <summary>Checks the canonical vocabulary and trimming through both public constructor paths.</summary>
    /// <param name="value">An approved category in its canonical case.</param>
    /// <param name="useCurrentTime">Whether to exercise the convenience overload instead of the controlled timestamp.</param>
    [Theory]
    [InlineData("Server", false)]
    [InlineData("Server", true)]
    [InlineData("Database", false)]
    [InlineData("Database", true)]
    [InlineData("Gateway", false)]
    [InlineData("Gateway", true)]
    [InlineData("SCADA", false)]
    [InlineData("SCADA", true)]
    public void Constructor_CanonicalDeviceTypes_AreAcceptedAfterTrimming(string value, bool useCurrentTime)
    {
        var id = Guid.NewGuid();
        string padded = $"  {value}  ";
        var device = useCurrentTime
            ? new MonitoredDevice(id, "EDM-SRV-001", "Application Server", padded, "EDM")
            : CreateDevice(id: id, deviceType: padded);

        Assert.Equal(value, device.DeviceType);
        Assert.Equal(id, device.Id);
        Assert.Equal(TimeSpan.Zero, device.CreatedAtUtc.Offset);
        Assert.NotEqual(default, device.CreatedAtUtc);
        Assert.Null(device.RetiredAtUtc);
        if (!useCurrentTime)
            Assert.Equal(CreatedAt, device.CreatedAtUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("server")]
    [InlineData("SERVER")]
    [InlineData("database")]
    [InlineData("gateway")]
    [InlineData("scada")]
    [InlineData("Router")]
    [InlineData("Ser ver")]
    public void Constructor_InvalidDeviceType_Throws(string? value)
    {
        var error = Assert.Throws<ArgumentException>(
            () => CreateDevice(deviceType: value!));
        Assert.Equal("deviceType", error.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_InvalidSiteCode_Throws(string? value)
    {
        Assert.Throws<ArgumentException>(
            () => CreateDevice(siteCode: value!));
    }

    [Theory]
    [InlineData(64)]
    [InlineData(65)]
    public void Constructor_AssetTag_LengthBoundary(int length)
    {
        string value = new('A', length);

        if (length == 64)
        {
            Assert.Equal(
                value,
                CreateDevice(assetTag: value).AssetTag);
        }
        else
        {
            Assert.Throws<ArgumentException>(
                () => CreateDevice(assetTag: value));
        }
    }

    [Theory]
    [InlineData(120)]
    [InlineData(121)]
    public void Constructor_DisplayName_LengthBoundary(int length)
    {
        string value = new('A', length);

        if (length == 120)
        {
            Assert.Equal(
                value,
                CreateDevice(displayName: value).DisplayName);
        }
        else
        {
            Assert.Throws<ArgumentException>(
                () => CreateDevice(displayName: value));
        }
    }

    /// <summary>Rejects arbitrary categories both at and beyond the persisted string length limit.</summary>
    /// <param name="length">Length of the unsupported category.</param>
    /// <remarks>A string of length 32 is not valid merely because it fits storage; category membership is also required.</remarks>
    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    public void Constructor_UnsupportedDeviceType_AtOrBeyondStorageLength_Throws(int length)
    {
        var error = Assert.Throws<ArgumentException>(
            () => CreateDevice(deviceType: new string('A', length)));
        Assert.Equal("deviceType", error.ParamName);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    public void Constructor_SiteCode_LengthBoundary(int length)
    {
        string value = new('A', length);

        if (length == 32)
        {
            Assert.Equal(
                value,
                CreateDevice(siteCode: value).SiteCode);
        }
        else
        {
            Assert.Throws<ArgumentException>(
                () => CreateDevice(siteCode: value));
        }
    }

    [Fact]
    public void Constructor_DefaultTimestamp_Throws()
    {
        // Call the constructor directly to ensure the invalid
        // default timestamp is passed without helper substitution.
        Assert.Throws<ArgumentException>(
            () => new MonitoredDevice(
                Guid.NewGuid(),
                "EDM-SRV-001",
                "Application Server",
                "Server",
                "EDM",
                default(DateTimeOffset)));
    }

    [Fact]
    public void Constructor_NonUtcTimestamp_Throws()
    {
        DateTimeOffset timestamp =
            new(2026, 10, 14, 12, 0, 0, TimeSpan.FromHours(-6));

        Assert.Throws<ArgumentException>(
            () => CreateDevice(createdAt: timestamp));
    }

    [Fact]
    public void Retire_ValidTimestamp_SetsRetirementTime()
    {
        MonitoredDevice device = CreateDevice();
        DateTimeOffset retiredAt = CreatedAt.AddDays(1);

        device.Retire(retiredAt);

        Assert.Equal(retiredAt, device.RetiredAtUtc);
    }

    [Fact]
    public void Retire_AtCreationTime_IsAllowed()
    {
        MonitoredDevice device = CreateDevice();

        device.Retire(CreatedAt);

        Assert.Equal(CreatedAt, device.RetiredAtUtc);
    }

    [Fact]
    public void Retire_BeforeCreation_ThrowsWithoutChangingState()
    {
        MonitoredDevice device = CreateDevice();

        Assert.Throws<ArgumentException>(
            () => device.Retire(CreatedAt.AddTicks(-1)));

        Assert.Null(device.RetiredAtUtc);
    }

    [Fact]
    public void Retire_DefaultTimestamp_ThrowsWithoutChangingState()
    {
        MonitoredDevice device = CreateDevice();

        Assert.Throws<ArgumentException>(
            () => device.Retire(default(DateTimeOffset)));

        Assert.Null(device.RetiredAtUtc);
    }

    [Fact]
    public void Retire_NonUtcTimestamp_ThrowsWithoutChangingState()
    {
        MonitoredDevice device = CreateDevice();

        DateTimeOffset invalidTimestamp =
            new(2026, 10, 15, 12, 0, 0, TimeSpan.FromHours(-6));

        Assert.Throws<ArgumentException>(
            () => device.Retire(invalidTimestamp));

        Assert.Null(device.RetiredAtUtc);
    }

    [Fact]
    public void Retire_RepeatedCall_PreservesOriginalTimestamp()
    {
        MonitoredDevice device = CreateDevice();
        DateTimeOffset firstRetirement = CreatedAt.AddDays(1);

        device.Retire(firstRetirement);
        device.Retire(CreatedAt.AddDays(2));

        Assert.Equal(firstRetirement, device.RetiredAtUtc);
    }

    [Fact]
    public void Retire_AlreadyRetired_InvalidTimestamp_IsIgnored()
    {
        MonitoredDevice device = CreateDevice();
        DateTimeOffset firstRetirement = CreatedAt.AddDays(1);

        device.Retire(firstRetirement);

        // Repeated retirement returns before timestamp validation.
        device.Retire(default(DateTimeOffset));

        Assert.Equal(firstRetirement, device.RetiredAtUtc);
    }

    [Fact]
    public void Retire_WithoutTimestamp_UsesCurrentUtcTime()
    {
        MonitoredDevice device = CreateDevice();

        DateTimeOffset before = DateTimeOffset.UtcNow;
        device.Retire();
        DateTimeOffset after = DateTimeOffset.UtcNow;

        Assert.NotNull(device.RetiredAtUtc);
        Assert.InRange(device.RetiredAtUtc.Value, before, after);
    }

    [Fact]
    public void Retire_WithoutTimestamp_RepeatedCallPreservesOriginalTimestamp()
    {
        MonitoredDevice device = CreateDevice();

        device.Retire();
        DateTimeOffset? originalRetirement = device.RetiredAtUtc;

        device.Retire();

        Assert.Equal(originalRetirement, device.RetiredAtUtc);
    }

    /// <summary>
    /// Verifies that surrounding whitespace is removed before
    /// maximum-length validation is performed.
    /// </summary>
    /// <remarks>
    /// Tags, names, and sites trim to their exact length limits. The raw category
    /// exceeds 32 characters but trims to Server, proving trimming precedes both
    /// length and category validation.
    /// </remarks>
    [Fact]
    public void Constructor_WhitespaceTrimming_PrecedesLengthValidation()
    {
        MonitoredDevice device = CreateDevice(
            assetTag: "  " + new string('a', 64) + "  ",
            displayName: "  " + new string('D', 120) + "  ",
            deviceType: new string(' ', 32) + "Server" + new string(' ', 32),
            siteCode: "  " + new string('s', 32) + "  ");

        Assert.Equal(new string('A', 64), device.AssetTag);
        Assert.Equal(new string('D', 120), device.DisplayName);
        Assert.Equal("Server", device.DeviceType);
        Assert.Equal(new string('S', 32), device.SiteCode);
    }    
}
