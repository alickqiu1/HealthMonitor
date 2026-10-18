using System.Transactions;
using HealthMonitor.Domain.Telemetry;
using HealthMonitor.Infrastructure.Data.TelemetryContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace HealthMonitor.UnitTests.Telemetry;

/// <summary>Exercises pre-admission guards without opening the deliberately unreachable provider connection.</summary>
public sealed class TelemetryRetentionStoreGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    /// <summary>Creates a configured context whose database must remain closed throughout these tests.</summary>
    private static TelemetryDbContext Context() => new(new DbContextOptionsBuilder<TelemetryDbContext>()
        .UseNpgsql("Host=example.invalid;Database=fictional;Username=unused").Options);
    /// <summary>Uses the caller's exact configured context so pre-admission checks prove ownership without I/O.</summary>
    private static TelemetryRetentionStore Store(TelemetryDbContext context) => new(context, NullLogger<TelemetryRetentionStore>.Instance);

    [Fact]
    public void Constructor_NullContext_Throws() => Assert.Throws<ArgumentNullException>(() => Store(null!));

    [Fact]
    public async Task Constructor_NullLogger_Throws()
    {
        await using var context = Context();
        Assert.Throws<ArgumentNullException>(() => new TelemetryRetentionStore(context, null!));
    }

    [Theory]
    [InlineData("ping_offset")]
    [InlineData("history_offset")]
    [InlineData("reversed")]
    public async Task InvalidCutoffs_RejectBeforeOwnershipOrConnection(string kind)
    {
        await using var context = Context();
        var adapter = Store(context);
        var ping = Now.AddHours(-24);
        var history = Now.AddDays(-7);
        if (kind == "ping_offset") ping = ping.ToOffset(TimeSpan.FromHours(1));
        if (kind == "history_offset") history = history.ToOffset(TimeSpan.FromHours(-1));
        if (kind == "reversed") history = Now;
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.PruneBatchAsync(ping, history));
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task PreCancelled_LeavesContextUnchanged()
    {
        await using var context = Context();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Store(context).PruneBatchAsync(Now.AddHours(-24), Now.AddDays(-7), cancellation.Token));
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task TrackedCallerWork_IsUntouchedAndRejectedWithoutInvalidatingContext()
    {
        await using var context = Context();
        var receipt = SimulationBatch.CreateCommitted(Guid.NewGuid(), new string('a', 64), "Healthy", Now, Now, 1);
        context.SimulationBatches.Add(receipt);
        var adapter = Store(context);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.PruneBatchAsync(Now.AddHours(-24), Now.AddDays(-7)));
            Assert.Same(receipt, Assert.Single(context.ChangeTracker.Entries()).Entity);
            Assert.Equal(EntityState.Added, context.Entry(receipt).State);
        }
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task AmbientTransaction_IsUntouchedAndRejectedBeforeConnection()
    {
        await using var context = Context();
        using var ambient = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        var transaction = Transaction.Current;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store(context).PruneBatchAsync(Now.AddHours(-24), Now.AddDays(-7)));
        Assert.Same(transaction, Transaction.Current);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }
}
