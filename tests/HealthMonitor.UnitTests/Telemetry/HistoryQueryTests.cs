using HealthMonitor.Application.Telemetry;
using HealthMonitor.Infrastructure.Data.TelemetryContext;
using Microsoft.EntityFrameworkCore;

namespace HealthMonitor.UnitTests.Telemetry;

/// <summary>Verifies immutable query inputs and adapter guards without a configured provider or database.</summary>
public sealed class HistoryQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, 1, 0L)]
    [InlineData(1, 100, 0L)]
    [InlineData(2, 100, 100L)]
    [InlineData(int.MaxValue, 100, 214748364600L)]
    public void Paging_ValidBoundaries_UseLongArithmetic(int page, int size, long offset)
    {
        var value = new HistoryPaging(page, size);
        Assert.Equal(page, value.Page); Assert.Equal(size, value.PageSize); Assert.Equal(offset, value.Offset);
    }

    [Theory]
    [InlineData(0, 25)]
    [InlineData(-1, 25)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 101)]
    public void Paging_InvalidInputs_Reject(int page, int size) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistoryPaging(page, size));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Range_ResolvesEachMissingEndpointAgainstOneInstant(int supplied)
    {
        var from = supplied is 1 or 3 ? Now.AddDays(-2) : (DateTimeOffset?)null;
        var to = supplied is 2 or 3 ? Now.AddDays(-1) : (DateTimeOffset?)null;
        var result = TelemetryHistoryRange.ForRequest(Now, from, to);
        Assert.Equal(to ?? Now, result.ToUtc);
        Assert.Equal(from ?? result.ToUtc.AddDays(-1), result.FromUtc);
    }

    [Fact]
    public void Range_ExactSevenDays_IsAllowed_OneTickMoreRejects()
    {
        var range = new TelemetryHistoryRange(Now.AddDays(-7), Now);
        Assert.Equal(TimeSpan.FromDays(7), range.ToUtc - range.FromUtc);
        Assert.Throws<ArgumentException>(() => new TelemetryHistoryRange(Now.AddDays(-7).AddTicks(-1), Now));
    }

    [Theory]
    [InlineData("equal")]
    [InlineData("reversed")]
    [InlineData("from-default")]
    [InlineData("to-default")]
    [InlineData("from-offset")]
    [InlineData("to-offset")]
    [InlineData("clock-default")]
    [InlineData("clock-offset")]
    [InlineData("underflow")]
    public void Range_InvalidUtcOrderingAndDefaultUnderflow_Reject(string kind)
    {
        var from = Now.AddHours(-1); var to = Now; var clock = Now;
        switch (kind)
        {
            case "equal": from = to; break;
            case "reversed": from = to.AddTicks(1); break;
            case "from-default": from = default; break;
            case "to-default": to = default; break;
            case "from-offset": from = from.ToOffset(TimeSpan.FromHours(1)); break;
            case "to-offset": to = to.ToOffset(TimeSpan.FromHours(1)); break;
            case "clock-default": clock = default; break;
            case "clock-offset": clock = clock.ToOffset(TimeSpan.FromHours(1)); break;
            case "underflow":
                Assert.Throws<ArgumentException>(() => TelemetryHistoryRange.ForRequest(clock, null, DateTimeOffset.MinValue.AddHours(1)));
                return;
        }
        Assert.Throws<ArgumentException>(() => TelemetryHistoryRange.ForRequest(clock, from, to));
    }

    [Fact]
    public void Range_ExplicitSmallAndFutureWindows_AreNotSilentlyRestricted()
    {
        Assert.NotNull(new TelemetryHistoryRange(DateTimeOffset.MinValue.AddTicks(1), DateTimeOffset.MinValue.AddHours(1)));
        Assert.NotNull(TelemetryHistoryRange.ForRequest(Now, Now.AddDays(1), Now.AddDays(2)));
    }

    [Fact]
    public async Task Adapter_InvalidInputs_RejectBeforeProvider()
    {
        Assert.Throws<ArgumentNullException>(() => new DeviceHistoryQueries(null!));
        await using var context = Context();
        var queries = new DeviceHistoryQueries(context);
        var paging = new HistoryPaging();
        var range = new TelemetryHistoryRange(Now.AddDays(-1), Now);
        await Assert.ThrowsAsync<ArgumentException>(() => queries.GetTelemetryAsync(Guid.Empty, paging, range));
        await Assert.ThrowsAsync<ArgumentException>(() => queries.GetIncidentsAsync(Guid.Empty, paging));
        await Assert.ThrowsAsync<ArgumentNullException>(() => queries.GetTelemetryAsync(Guid.NewGuid(), null!, range));
        await Assert.ThrowsAsync<ArgumentNullException>(() => queries.GetTelemetryAsync(Guid.NewGuid(), paging, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => queries.GetIncidentsAsync(Guid.NewGuid(), null!));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => queries.GetIncidentsAsync(Guid.NewGuid(), paging, (IncidentHistoryState)99));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adapter_PreCancellation_RejectsBeforeProvider(bool incidents)
    {
        await using var context = Context();
        var queries = new DeviceHistoryQueries(context);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => incidents
            ? (Task)queries.GetIncidentsAsync(Guid.NewGuid(), new(), cancellationToken: cancellation.Token)
            : queries.GetTelemetryAsync(Guid.NewGuid(), new(), new(Now.AddDays(-1), Now), cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adapter_AmbientTransaction_RejectsWithoutEnlistingOrProvider(bool incidents)
    {
        await using var context = Context();
        using var scope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
        var queries = new DeviceHistoryQueries(context);
        await Assert.ThrowsAsync<InvalidOperationException>(() => incidents
            ? (Task)queries.GetIncidentsAsync(Guid.NewGuid(), new())
            : queries.GetTelemetryAsync(Guid.NewGuid(), new(), new(Now.AddDays(-1), Now)));
        Assert.NotNull(System.Transactions.Transaction.Current);
    }

    /// <summary>Deliberately has no provider: guards must finish before any connection-dependent operation.</summary>
    private static TelemetryDbContext Context() => new(new DbContextOptionsBuilder<TelemetryDbContext>().Options);
}
