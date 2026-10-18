using HealthMonitor.Api.Fleet;
using HealthMonitor.Application.Device;
using HealthMonitor.Application.IntegrationContracts;
using HealthMonitor.Application.Telemetry;
using HealthMonitor.Domain.Telemetry;

namespace HealthMonitor.UnitTests.Fleet;

/// <summary>Checks history composition validation, clock ownership, and catalog gating without a database or HTTP host.</summary>
public sealed class DeviceHistoryReadServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        var fake = new Ports();
        Assert.Throws<ArgumentNullException>(() => new DeviceHistoryReadService(null!, fake, fake));
        Assert.Throws<ArgumentNullException>(() => new DeviceHistoryReadService(fake, null!, fake));
        Assert.Throws<ArgumentNullException>(() => new DeviceHistoryReadService(fake, fake, null!));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnknownOrEmpty_StopsBeforeHistory(bool incidents, bool empty)
    {
        var fake = new Ports { Known = false };
        var result = await Read(fake, incidents, empty ? Guid.Empty : fake.Id);
        Assert.Null(result);
        Assert.Equal(empty ? 0 : 1, fake.CatalogReads);
        Assert.Equal(0, fake.HistoryReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownRetired_StillReadsEmptyHistory_WithOneClockBeforeCatalog(bool incidents)
    {
        var fake = new Ports { Retired = true };
        var result = await Read(fake, incidents, fake.Id);
        Assert.NotNull(result);
        Assert.Equal(1, fake.ClockReads);
        Assert.Equal(1, fake.CatalogReads);
        Assert.Equal(1, fake.HistoryReads);
        Assert.Equal(Now, fake.CapturedAsOf);
        if (!incidents)
        {
            Assert.Equal(Now.AddDays(-1), fake.Range!.FromUtc);
            Assert.Equal(Now, fake.Range.ToUtc);
        }
    }

    [Theory]
    [InlineData(false, 0, 25)]
    [InlineData(true, 0, 25)]
    [InlineData(false, 1, 101)]
    [InlineData(true, 1, 101)]
    public async Task InvalidPaging_RejectsBeforeCatalog(bool incidents, int page, int size)
    {
        var fake = new Ports();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => incidents
            ? (Task)Service(fake).ReadIncidentsAsync(fake.Id, page, size)
            : Service(fake).ReadTelemetryAsync(fake.Id, page, size));
        Assert.Equal(0, fake.CatalogReads);
    }

    [Fact]
    public async Task InvalidRangeAndState_RejectBeforeCatalog()
    {
        var fake = new Ports();
        await Assert.ThrowsAsync<ArgumentException>(() => Service(fake).ReadTelemetryAsync(fake.Id, fromUtc: Now, toUtc: Now));
        await Assert.ThrowsAsync<ArgumentException>(() => Service(fake).ReadTelemetryAsync(fake.Id, fromUtc: Now.AddDays(-8), toUtc: Now));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Service(fake).ReadIncidentsAsync(fake.Id, state: (IncidentHistoryState)99));
        Assert.Equal(0, fake.CatalogReads);
    }

    [Theory]
    [InlineData(IncidentHistoryState.All)]
    [InlineData(IncidentHistoryState.Open)]
    [InlineData(IncidentHistoryState.Resolved)]
    public async Task Incidents_ForwardStateAndPagingWithoutDateFilter(IncidentHistoryState state)
    {
        var fake = new Ports();
        var result = await Service(fake).ReadIncidentsAsync(fake.Id, 3, 100, state);
        Assert.Equal(state, fake.State); Assert.Equal(3, fake.Paging!.Page); Assert.Equal(100, fake.Paging.PageSize);
        Assert.Equal(Now, result!.AsOfUtc);
        Assert.Null(fake.Range);
    }

    [Fact]
    public async Task ExplicitTelemetryWindowAndFractionalTimes_AreForwardedUnchanged()
    {
        var fake = new Ports();
        var from = Now.AddHours(-2).AddTicks(1); var to = Now.AddHours(-1).AddTicks(1);
        var result = await Service(fake).ReadTelemetryAsync(fake.Id, int.MaxValue, 100, from, to);
        Assert.Equal(from, fake.Range!.FromUtc); Assert.Equal(to, fake.Range.ToUtc);
        Assert.Equal(214748364600L, fake.Paging!.Offset);
        Assert.Equal(Now, result!.AsOfUtc);
    }

    [Theory]
    [InlineData(false, "before")]
    [InlineData(true, "before")]
    [InlineData(false, "catalog")]
    [InlineData(true, "catalog")]
    [InlineData(false, "history")]
    [InlineData(true, "history")]
    public async Task Cancellation_PropagatesAtEveryBoundary(bool incidents, string when)
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new Ports();
        if (when == "before") cancellation.Cancel();
        if (when == "catalog") fake.OnCatalog = cancellation.Cancel;
        if (when == "history") fake.OnHistory = cancellation.Cancel;
        var failure = await Assert.ThrowsAsync<OperationCanceledException>(() => incidents
            ? (Task)Service(fake).ReadIncidentsAsync(fake.Id, cancellationToken: cancellation.Token)
            : Service(fake).ReadTelemetryAsync(fake.Id, cancellationToken: cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(when == "before" ? 0 : 1, fake.CatalogReads);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ProviderFailures_PropagateSameException(bool incidents, bool history)
    {
        var fake = new Ports(); var failure = new InvalidOperationException("owned fake failure");
        if (history) fake.OnHistory = () => throw failure; else fake.OnCatalog = () => throw failure;
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => Read(fake, incidents, fake.Id)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MismatchedCatalog_RejectsBeforeHistory(bool incidents)
    {
        var fake = new Ports { WrongCatalog = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(fake, incidents, fake.Id));
        Assert.Equal(0, fake.HistoryReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrossDevicePage_RejectsInsteadOfLeakingData(bool incidents)
    {
        var fake = new Ports { WrongHistory = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(fake, incidents, fake.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReturnedCollections_AreDefensivelyFrozen(bool incidents)
    {
        var fake = new Ports { IncludeItem = true };
        if (incidents)
        {
            var result = await Service(fake).ReadIncidentsAsync(fake.Id);
            var items = Assert.IsAssignableFrom<IList<IncidentHistoryItem>>(result!.Page.Items);
            Assert.Throws<NotSupportedException>(() => items.Clear());
            fake.Incidents.Clear(); Assert.Single(result.Page.Items);
        }
        else
        {
            var result = await Service(fake).ReadTelemetryAsync(fake.Id);
            var items = Assert.IsAssignableFrom<IList<TelemetryHistoryItem>>(result!.Page.Items);
            Assert.Throws<NotSupportedException>(() => items.Clear());
            fake.Pings.Clear(); Assert.Single(result.Page.Items);
        }
    }

    [Theory]
    [InlineData(false, "negative")]
    [InlineData(true, "negative")]
    [InlineData(false, "wrong-page")]
    [InlineData(true, "wrong-page")]
    [InlineData(false, "wrong-size")]
    [InlineData(true, "wrong-size")]
    [InlineData(false, "incomplete")]
    [InlineData(true, "incomplete")]
    [InlineData(false, "beyond-end")]
    [InlineData(true, "beyond-end")]
    public async Task InconsistentPageEnvelope_IsRejected(bool incidents, string fault)
    {
        var fake = new Ports();
        if (fault == "negative") fake.Total = -1;
        if (fault == "wrong-page") fake.ResultPaging = new(2, 25);
        if (fault == "wrong-size") fake.ResultPaging = new(1, 1);
        if (fault == "incomplete") fake.Total = 1;
        if (fault == "beyond-end") { fake.Total = 1; fake.IncludeItem = true; }
        int page = fault == "beyond-end" ? 2 : 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => incidents
            ? (Task)Service(fake).ReadIncidentsAsync(fake.Id, page)
            : Service(fake).ReadTelemetryAsync(fake.Id, page));
    }
    /// <summary>Invokes one concrete operation while retaining its result for null/exception assertions.</summary>
    private static async Task<object?> Read(Ports fake, bool incidents, Guid id) => incidents
        ? await Service(fake).ReadIncidentsAsync(id) : await Service(fake).ReadTelemetryAsync(id);

    /// <summary>Uses the real scoped composer with one fake implementing independently called application ports.</summary>
    private static DeviceHistoryReadService Service(Ports fake) => new(fake, fake, fake);

    /// <summary>Records query ordering and forwards controlled facts; clock advances during catalog access to expose recapture bugs.</summary>
    private sealed class Ports : TimeProvider, IDeviceCatalogQueries, IDeviceHistoryQueries
    {
        internal readonly Guid Id = Guid.NewGuid();
        internal bool Known = true, Retired, WrongCatalog, WrongHistory, IncludeItem;
        internal int ClockReads, CatalogReads, HistoryReads;
        internal DateTimeOffset CapturedAsOf;
        internal HistoryPaging? Paging, ResultPaging;
        internal long? Total;
        internal TelemetryHistoryRange? Range;
        internal IncidentHistoryState State;
        internal Action? OnCatalog, OnHistory;
        internal readonly List<TelemetryHistoryItem> Pings = new();
        internal readonly List<IncidentHistoryItem> Incidents = new();

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() { ClockReads++; CapturedAsOf = Now; return CatalogReads == 0 ? Now : Now.AddHours(1); }
        /// <inheritdoc />
        public Task<DeviceCatalogDetail?> GetDetailAsync(Guid deviceId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(1, ClockReads); Assert.Equal(Id, deviceId); CatalogReads++; OnCatalog?.Invoke();
            return Task.FromResult(Known ? new DeviceCatalogDetail(
                new DeviceRegistration(WrongCatalog ? Guid.NewGuid() : Id, "History", "HISTORY", "DEN", "Server", Retired),
                Now.AddDays(-2), Retired ? Now.AddDays(-1) : null) : null);
        }
        /// <inheritdoc />
        public Task<HistoryPage<TelemetryHistoryItem>> GetTelemetryAsync(Guid deviceId, HistoryPaging paging,
            TelemetryHistoryRange range, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Id, deviceId); HistoryReads++; Paging = paging; Range = range; OnHistory?.Invoke();
            if (WrongHistory || IncludeItem) Pings.Add(new(Guid.NewGuid(), WrongHistory ? Guid.NewGuid() : Id, Now, PingOutcome.Success, 40, 30));
            return Task.FromResult(new HistoryPage<TelemetryHistoryItem>(Pings, ResultPaging ?? paging, Total ?? Pings.Count));
        }
        /// <inheritdoc />
        public Task<HistoryPage<IncidentHistoryItem>> GetIncidentsAsync(Guid deviceId, HistoryPaging paging,
            IncidentHistoryState state = IncidentHistoryState.All, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Id, deviceId); HistoryReads++; Paging = paging; State = state; OnHistory?.Invoke();
            if (WrongHistory || IncludeItem) Incidents.Add(new(Guid.NewGuid(), WrongHistory ? Guid.NewGuid() : Id, IncidentKind.Cpu, Now, null, "CPU"));
            return Task.FromResult(new HistoryPage<IncidentHistoryItem>(Incidents, ResultPaging ?? paging, Total ?? Incidents.Count));
        }
        /// <inheritdoc />
        public Task<IReadOnlyList<DeviceRegistration>> GetActiveAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("History must not list the fleet.");
        /// <inheritdoc />
        public Task<IReadOnlyList<DeviceRegistration>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("History uses explicit detail to establish inventory existence.");
    }
}
