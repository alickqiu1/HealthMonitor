using HealthMonitor.Api.Fleet;
using HealthMonitor.Domain.Telemetry;

namespace HealthMonitor.UnitTests.Fleet;

/// <summary>Verifies complete-fleet query semantics and stable typed ordering before any HTTP integration.</summary>
public sealed class DeviceGridPagingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NullInputs_Reject()
    {
        Assert.Throws<ArgumentNullException>(() => DeviceGridPaging.Apply(null!, new()));
        Assert.Throws<ArgumentNullException>(() => DeviceGridPaging.Apply(Snapshot(), null!));
    }

    [Fact]
    public void EmptyFleetAndHugePages_PreserveCoordinatesAndTrueCount()
    {
        var empty = DeviceGridPaging.Apply(Snapshot(), new(int.MaxValue, 100));
        Assert.Empty(empty.Items); Assert.Equal(0, empty.TotalCount);
        Assert.Equal(int.MaxValue, empty.Page); Assert.Equal(100, empty.PageSize); Assert.Equal(Now, empty.AsOfUtc);
        var snapshot = Snapshot(Row(1), Row(2));
        var page = DeviceGridPaging.Apply(snapshot, new(int.MaxValue, 100));
        Assert.Empty(page.Items); Assert.Equal(2, page.TotalCount);
    }

    [Fact]
    public void GlobalAndFilters_ApplyBeforeCountAndPage_WithoutChangingMetricsOrSnapshot()
    {
        var rows = new[]
        {
            Row(1) with { DisplayName = "Pump", SiteCode = "EDM", DeviceType = "Server", Status = HealthState.Degraded },
            Row(2) with { DisplayName = "Pump", SiteCode = "DEN", DeviceType = "Server", Status = HealthState.Healthy },
            Row(3) with { DisplayName = "Other", SiteCode = "DEN", DeviceType = "Server", Status = HealthState.Degraded },
            Row(4) with { DisplayName = "PUMP", SiteCode = "DEN", DeviceType = "Database", Status = HealthState.Degraded },
            Row(5) with { DisplayName = "pump one", SiteCode = "DEN", DeviceType = "Server", Status = HealthState.Degraded },
            Row(6) with { DisplayName = "pump two", SiteCode = "DEN", DeviceType = "Server", Status = HealthState.Degraded }
        };
        var original = rows.ToArray();
        var snapshot = Snapshot(rows);
        var metrics = snapshot.Metrics;
        var page = DeviceGridPaging.Apply(snapshot, new(2, 1, " pump ", "den", "server", "Degraded"));
        Assert.Equal(2, page.TotalCount); Assert.Equal(rows[5], Assert.Single(page.Items));
        Assert.Equal(Now, page.AsOfUtc); Assert.Same(metrics, snapshot.Metrics);
        Assert.Equal(6, snapshot.Metrics.Total); Assert.Equal(original, snapshot.Rows);
        Assert.Equal(original, rows);
        Assert.Throws<NotSupportedException>(() => ((IList<FleetRow>)page.Items).Clear());
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("[")]
    [InlineData("'")]
    [InlineData("MiXeD")]
    public void LiteralSearch_MatchesNameOrTag_WithoutWildcardInterpretation(string search)
    {
        var target = Row(1) with { AssetTag = $"tag-{search.ToLowerInvariant()}-suffix" };
        var other = Row(2) with { DisplayName = "unrelated", AssetTag = "ordinary" };
        var page = DeviceGridPaging.Apply(Snapshot(other, target), new(q: search));
        Assert.Equal(target, Assert.Single(page.Items)); Assert.Equal(1, page.TotalCount);
        var byName = target with { AssetTag = "ordinary", DisplayName = $"name-{search}-suffix" };
        Assert.Equal(byName, Assert.Single(DeviceGridPaging.Apply(Snapshot(other, byName), new(q: search)).Items));
    }

    [Fact]
    public void KnownEmptySite_IsValid_AndOtherSiteRowsRemainVisibleWithoutFilter()
    {
        var other = Row(1) with { SiteCode = "LON" };
        var snapshot = Snapshot(other);
        Assert.Equal(other, Assert.Single(DeviceGridPaging.Apply(snapshot, new()).Items));
        foreach (var site in new[] { "EDM", "CGY", "DEN" })
        {
            var page = DeviceGridPaging.Apply(snapshot, new(site: site));
            Assert.Empty(page.Items); Assert.Equal(0, page.TotalCount);
        }
    }

    [Theory]
    [InlineData("displayName", "asc")]
    [InlineData("displayName", "desc")]
    [InlineData("assetTag", "asc")]
    [InlineData("assetTag", "desc")]
    [InlineData("siteCode", "asc")]
    [InlineData("siteCode", "desc")]
    [InlineData("status", "asc")]
    [InlineData("status", "desc")]
    [InlineData("lastObservedAtUtc", "asc")]
    [InlineData("lastObservedAtUtc", "desc")]
    [InlineData("latencyMs", "asc")]
    [InlineData("latencyMs", "desc")]
    [InlineData("cpuPercent", "asc")]
    [InlineData("cpuPercent", "desc")]
    public void EverySort_UsesTypedOrderingAndAscendingUuidTies(string sort, string direction)
    {
        var low1 = Row(1) with { DisplayName = "alpha", AssetTag = "alpha", SiteCode = "alpha", Status = HealthState.Offline,
            LastObservedAtUtc = Now.AddSeconds(-1), LatencyMs = 2, CpuPercent = 2.5m };
        var low2 = low1 with { DeviceId = Row(2).DeviceId, DisplayName = "ALPHA", AssetTag = "ALPHA", SiteCode = "ALPHA" };
        var high = Row(3) with { DisplayName = "zulu", AssetTag = "zulu", SiteCode = "zulu", Status = HealthState.Healthy,
            LastObservedAtUtc = Now, LatencyMs = 10, CpuPercent = 10.5m };
        var source = new[] { high, low2, low1 };
        var page = DeviceGridPaging.Apply(Snapshot(source), new(sort: sort, direction: direction));
        Assert.Equal(direction == "asc" ? new[] { low1, low2, high } : new[] { high, low1, low2 }, page.Items);
        Assert.Equal(new[] { high, low2, low1 }, source);
    }

    [Theory]
    [InlineData("lastObservedAtUtc", "asc")]
    [InlineData("lastObservedAtUtc", "desc")]
    [InlineData("latencyMs", "asc")]
    [InlineData("latencyMs", "desc")]
    [InlineData("cpuPercent", "asc")]
    [InlineData("cpuPercent", "desc")]
    public void NullableSorts_AlwaysPutNullLastAndKeepUuidTies(string sort, string direction)
    {
        var low = Row(3) with { LastObservedAtUtc = Now.AddSeconds(-1), LatencyMs = 0, CpuPercent = 0 };
        var high = Row(4) with { LastObservedAtUtc = Now, LatencyMs = 10, CpuPercent = 10 };
        var absent1 = Row(1) with { Status = HealthState.Unknown, LastObservedAtUtc = null, LatencyMs = null, CpuPercent = null };
        var absent2 = absent1 with { DeviceId = Row(2).DeviceId };
        var page = DeviceGridPaging.Apply(Snapshot(absent2, high, absent1, low), new(sort: sort, direction: direction));
        Assert.Equal(direction == "asc" ? new[] { low, high, absent1, absent2 } : new[] { high, low, absent1, absent2 }, page.Items);
    }

    [Theory]
    [InlineData("asc")]
    [InlineData("desc")]
    public void Status_UsesExplicitSeverityRatherThanEnumOrder(string direction)
    {
        var offline = Row(1) with { Status = HealthState.Offline };
        var degraded = Row(2) with { Status = HealthState.Degraded };
        var unknown = Row(3) with { Status = HealthState.Unknown };
        var healthy = Row(4) with { Status = HealthState.Healthy };
        var ascending = new[] { offline, degraded, unknown, healthy };
        var page = DeviceGridPaging.Apply(Snapshot(healthy, unknown, offline, degraded), new(sort: "status", direction: direction));
        Assert.Equal(direction == "asc" ? ascending : ascending.Reverse(), page.Items);
    }

    [Fact]
    public void FilteringFindsRowsBeyondAnUnfilteredPage_AndPageBoundaryIsExact()
    {
        var rows = Enumerable.Range(1, 30).Select(i => Row(i) with { DisplayName = $"device {i:D2}" }).ToArray();
        var snapshot = Snapshot(rows);
        Assert.Equal(rows.Take(25), DeviceGridPaging.Apply(snapshot, new()).Items);
        Assert.Equal(rows.Skip(25), DeviceGridPaging.Apply(snapshot, new(2)).Items);
        Assert.Empty(DeviceGridPaging.Apply(snapshot, new(3)).Items);
        var found = DeviceGridPaging.Apply(snapshot, new(q: "device 30"));
        Assert.Equal(rows[29], Assert.Single(found.Items)); Assert.Equal(1, found.TotalCount);
    }

    /// <summary>Creates deterministic UUIDs so ordering expectations are independent of input order and random seeds.</summary>
    private static FleetRow Row(int number) => new(Guid.Parse($"00000000-0000-0000-0000-{number:D12}"),
        "Device", "TAG", "DEN", "Server", HealthState.Healthy, Now, 40, 30);

    /// <summary>Uses caller-owned row storage deliberately to detect accidental in-place sorting; metrics remain global.</summary>
    private static FleetSnapshot Snapshot(params FleetRow[] rows) => new(Now, rows,
        new FleetMetrics(rows.Length, rows.Count(r => r.Status == HealthState.Healthy), rows.Count(r => r.Status == HealthState.Degraded),
            rows.Count(r => r.Status == HealthState.Offline), rows.Count(r => r.Status == HealthState.Unknown), null, 0));
}
