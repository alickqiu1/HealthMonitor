using HealthMonitor.Api.Fleet;
using HealthMonitor.Domain.Telemetry;

namespace HealthMonitor.UnitTests.Fleet;

/// <summary>Verifies the API's exact query allowlists and normalization without HTTP, persistence, or time.</summary>
public sealed class DeviceGridQueryTests
{
    [Fact]
    public void Defaults_AreNeutralAndCanonical()
    {
        var query = new DeviceGridQuery();
        Assert.Equal(1, query.Page); Assert.Equal(25, query.PageSize);
        Assert.Null(query.Q); Assert.Null(query.Site); Assert.Null(query.Type); Assert.Null(query.Status);
        Assert.Equal(DeviceGridSort.DisplayName, query.Sort); Assert.False(query.Descending);
    }

    [Theory]
    [InlineData(1, 1, 0L)]
    [InlineData(1, 100, 0L)]
    [InlineData(int.MaxValue, 100, 214748364600L)]
    public void ValidPaging_UsesLongOffset(int page, int size, long offset) =>
        Assert.Equal(offset, new DeviceGridQuery(page, size).Offset);

    [Theory]
    [InlineData(0, 25)]
    [InlineData(-1, 25)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public void InvalidPaging_Rejects(int page, int size) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeviceGridQuery(page, size));

    [Fact]
    public void Search_TrimsBeforeLengthValidation_OnlyEmptySearchIsNeutral()
    {
        Assert.Null(new DeviceGridQuery(q: " \t ").Q);
        Assert.Equal(new string('x', 100), new DeviceGridQuery(q: " " + new string('x', 100) + " ").Q);
        Assert.Throws<ArgumentException>(() => new DeviceGridQuery(q: new string('x', 101)));
        Assert.Equal("%_['", new DeviceGridQuery(q: " %_[' ").Q);
    }

    [Theory]
    [InlineData(" edm ", "EDM")]
    [InlineData("cGy", "CGY")]
    [InlineData("DEN", "DEN")]
    public void Site_NormalizesToSupportedFixedSite(string input, string expected) =>
        Assert.Equal(expected, new DeviceGridQuery(site: input).Site);

    [Theory]
    [InlineData(" server ", "Server")]
    [InlineData("DATABASE", "Database")]
    [InlineData("gateway", "Gateway")]
    [InlineData("scada", "SCADA")]
    public void Type_NormalizesToCanonicalCategory(string input, string expected) =>
        Assert.Equal(expected, new DeviceGridQuery(type: input).Type);

    [Theory]
    [InlineData("Unknown", HealthState.Unknown)]
    [InlineData("Healthy", HealthState.Healthy)]
    [InlineData("Degraded", HealthState.Degraded)]
    [InlineData("Offline", HealthState.Offline)]
    public void Status_AcceptsOnlyCanonicalTokens(string input, HealthState expected) =>
        Assert.Equal(expected, new DeviceGridQuery(status: input).Status);

    [Theory]
    [InlineData("displayName", (int)DeviceGridSort.DisplayName)]
    [InlineData("assetTag", (int)DeviceGridSort.AssetTag)]
    [InlineData("siteCode", (int)DeviceGridSort.SiteCode)]
    [InlineData("status", (int)DeviceGridSort.Status)]
    [InlineData("lastObservedAtUtc", (int)DeviceGridSort.LastObservedAtUtc)]
    [InlineData("latencyMs", (int)DeviceGridSort.LatencyMs)]
    [InlineData("cpuPercent", (int)DeviceGridSort.CpuPercent)]
    public void Sort_AcceptsExplicitOperations(string input, int expected) =>
        Assert.Equal((DeviceGridSort)expected, new DeviceGridQuery(sort: input).Sort);

    [Theory]
    [InlineData("asc", false)]
    [InlineData("desc", true)]
    public void Direction_AcceptsCanonicalTokens(string input, bool expected) =>
        Assert.Equal(expected, new DeviceGridQuery(direction: input).Descending);

    [Theory]
    [InlineData("site", "LON")]
    [InlineData("site", "")]
    [InlineData("site", " ")]
    [InlineData("type", "Router")]
    [InlineData("type", "")]
    [InlineData("status", "0")]
    [InlineData("status", "healthy")]
    [InlineData("status", "")]
    [InlineData("sort", "DisplayName")]
    [InlineData("sort", "id")]
    [InlineData("sort", "displayName; DROP TABLE devices")]
    [InlineData("sort", "")]
    [InlineData("sort", " ")]
    [InlineData("direction", "DESC")]
    [InlineData("direction", "0")]
    [InlineData("direction", "")]
    public void UnknownOrSuppliedBlankTokens_RejectWithOwnedParameter(string field, string input)
    {
        var error = Assert.Throws<ArgumentException>(() => field switch
        {
            "site" => new DeviceGridQuery(site: input),
            "type" => new DeviceGridQuery(type: input),
            "status" => new DeviceGridQuery(status: input),
            "sort" => new DeviceGridQuery(sort: input),
            _ => new DeviceGridQuery(direction: input)
        });
        Assert.Equal(field, error.ParamName);
    }
}
