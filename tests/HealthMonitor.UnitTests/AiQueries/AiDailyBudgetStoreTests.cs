using System.Text;
using System.Text.Json;
using HealthMonitor.Api.AiQueries;
using Xunit;
using static HealthMonitor.Api.AiQueries.AiDailyBudgetStore;

namespace HealthMonitor.UnitTests.AiQueries;

/// <summary>Uses only uniquely owned temporary files and a controlled UTC clock to verify conservative daily reservations.</summary>
public sealed class AiDailyBudgetStoreTests
{
    [Fact]
    public void Constructor_RejectsRelativePathAndNullClock()
    {
        Assert.Throws<ArgumentException>(() => new AiDailyBudgetStore("private-canary", new Clock()));
        using var files = new Files();
        Assert.Throws<ArgumentNullException>(() => new AiDailyBudgetStore(files.Counter, null!));
    }

    [Fact]
    public async Task Reservation_PersistsAndSurvivesRestartAndLaterCancellation()
    {
        using var files = new Files(); files.Seed(42);
        using var abort = new CancellationTokenSource();
        await using (var store = files.Open())
        {
            Assert.Equal(Outcome.Admitted, await store.TryReserveAsync(abort.Token));
            Assert.Equal(43, files.Count()); abort.Cancel();
            Assert.Equal(43, files.Count());
        }
        await using var restarted = files.Open();
        Assert.Equal(Outcome.Admitted, await restarted.TryReserveAsync());
        Assert.Equal(44, files.Count());
        Assert.True(File.Exists(files.Counter + ".lock"));
        Assert.False(File.Exists(files.Counter + ".pending"));
        // Post-return cancellation proves no refund, not cancellation racing exactly with atomic replacement.
    }

    [Fact]
    public async Task ConcurrentLastSlot_OneAdmissionAndExactLimitPreserved()
    {
        using var files = new Files(); files.Seed(99);
        await using var store = files.Open();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => store.TryReserveAsync()));
        Assert.Equal(1, results.Count(result => result == Outcome.Admitted));
        Assert.Equal(11, results.Count(result => result == Outcome.Exhausted));
        Assert.Equal(100, files.Count());
        string before = File.ReadAllText(files.Counter);
        Assert.Equal(Outcome.Exhausted, await store.TryReserveAsync());
        Assert.Equal(before, File.ReadAllText(files.Counter));
    }

    [Fact]
    public async Task UtcMidnight_RollsOverOnlyWithPersistedReservationAndClockRollbackDisables()
    {
        using var files = new Files(); files.Seed(100);
        files.Clock.Now = new(2026, 10, 18, 23, 59, 59, TimeSpan.Zero);
        await using var store = files.Open();
        Assert.Equal(Outcome.Exhausted, await store.TryReserveAsync());
        files.Clock.Now = files.Clock.Now.AddSeconds(1);
        Assert.Equal(Outcome.Admitted, await store.TryReserveAsync());
        Assert.Equal(1, files.Count()); Assert.Equal("2026-10-19", files.Day());
        files.Clock.Now = files.Clock.Now.AddSeconds(-1);
        Assert.Equal(Outcome.Unavailable, await store.TryReserveAsync());
        Assert.Equal(1, files.Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderFileReplacement_CannotEvadePairedFloor(bool acrossMidnight)
    {
        using var files = new Files(); files.Seed(40);
        await using var store = files.Open();
        Assert.Equal(Outcome.Admitted, await store.TryReserveAsync());
        if (acrossMidnight)
        {
            files.Clock.Now = files.Clock.Now.AddDays(1);
            Assert.Equal(Outcome.Admitted, await store.TryReserveAsync());
        }
        files.Seed(0, acrossMidnight ? "2026-10-18" : "2026-10-17");
        string before = File.ReadAllText(files.Counter);
        Assert.Equal(Outcome.Unavailable, await store.TryReserveAsync());
        Assert.Equal(before, File.ReadAllText(files.Counter));
    }

    [Fact]
    public async Task SameDayFloor_AllowsHigherCountButRejectsDecreaseAndLatchesAfterRepair()
    {
        using var files = new Files(); files.Seed(40);
        await using var store = files.Open();
        Assert.Equal(Outcome.Admitted, await store.TryReserveAsync());
        files.Seed(80);
        Assert.Equal(Outcome.Admitted, await store.TryReserveAsync()); Assert.Equal(81, files.Count());
        files.Seed(80);
        Assert.Equal(Outcome.Unavailable, await store.TryReserveAsync());
        files.Seed(100);
        Assert.Equal(Outcome.Unavailable, await store.TryReserveAsync()); Assert.Equal(100, files.Count());
    }

    [Fact]
    public async Task MissingState_IsNotBootstrapAndRepairRequiresNewLifetime()
    {
        using var files = new Files();
        await using (var store = files.Open())
        {
            Assert.Equal(Outcome.Unavailable, await store.TryReserveAsync());
            Assert.False(File.Exists(files.Counter));
            files.Seed(50);
            Assert.Equal(Outcome.Unavailable, await store.TryReserveAsync()); Assert.Equal(50, files.Count());
        }
        await using var repaired = files.Open();
        Assert.Equal(Outcome.Admitted, await repaired.TryReserveAsync()); Assert.Equal(51, files.Count());
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public async Task ActualByteBoundary_IsInclusive(int length, bool admitted)
    {
        using var files = new Files(); files.Seed(0);
        string json = File.ReadAllText(files.Counter);
        File.WriteAllText(files.Counter, json.PadRight(length), new UTF8Encoding(false));
        await using var store = files.Open();
        Assert.Equal(admitted ? Outcome.Admitted : Outcome.Unavailable, await store.TryReserveAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"version\":1,\"day\":\"2026-10-18\"}")]
    [InlineData("{\"version\":2,\"day\":\"2026-10-18\",\"count\":0}")]
    [InlineData("{\"version\":1,\"day\":\"2026-10-19\",\"count\":0}")]
    [InlineData("{\"version\":1,\"day\":\"2026-10-18T00:00:00Z\",\"count\":0}")]
    [InlineData("{\"version\":1,\"day\":\"2026-02-30\",\"count\":0}")]
    [InlineData("{\"version\":1,\"day\":null,\"count\":0}")]
    [InlineData("{\"version\":1,\"day\":\"2026-10-18\",\"count\":-1}")]
    [InlineData("{\"version\":1,\"day\":\"2026-10-18\",\"count\":101}")]
    [InlineData("{\"version\":1,\"day\":\"2026-10-18\",\"count\":1.5}")]
    [InlineData("{\"version\":1,\"day\":\"2026-10-18\",\"count\":\"0\"}")]
    [InlineData("{\"Version\":1,\"day\":\"2026-10-18\",\"count\":0}")]
    [InlineData("{\"version\":1,\"day\":\"2026-10-18\",\"count\":0,\"count\":1}")]
    [InlineData("{\"version\":1,\"day\":\"2026-10-18\",\"count\":0,\"co\\u0075nt\":1}")]
    [InlineData("{\"version\":1,\"day\":\"2026-10-18\",\"count\":0,\"extra\":true}")]
    [InlineData("{\"version\":1,\"day\":\"2026-10-18\",\"count\":0,}")]
    [InlineData("{\"version\":1,\"day\":\"2026-10-18\",\"count\":0} null")]
    [InlineData("{\"version\":1,\"day\":\"\\uD800\",\"count\":0}")]
    [InlineData("{\"version\":1,\"day\":\"\\uDC00\",\"count\":0}")]
    [InlineData("{\"version\":1,\"day\":\"2026-10-18\",\"count\":0,\"\\uD800\":0}")]
    [InlineData("{\"version\":1,\"day\":\"2026-10-18\",\"count\":0,\"\\uDC00\":0}")]
    public async Task InvalidState_DisablesWithoutChangingBytes(string json)
    {
        using var files = new Files(); File.WriteAllText(files.Counter, json, new UTF8Encoding(false));
        byte[] before = File.ReadAllBytes(files.Counter);
        await using var store = files.Open();
        Assert.Equal(Outcome.Unavailable, await store.TryReserveAsync());
        Assert.Equal(before, File.ReadAllBytes(files.Counter));
        files.Seed(0);
        Assert.Equal(Outcome.Unavailable, await store.TryReserveAsync()); Assert.Equal(0, files.Count());
    }

    [Fact]
    public async Task InvalidUtf8_DisablesWithoutReplacingFile()
    {
        using var files = new Files(); File.WriteAllBytes(files.Counter, [0xff]);
        await using var store = files.Open();
        Assert.Equal(Outcome.Unavailable, await store.TryReserveAsync());
        Assert.Equal(new byte[] { 0xff }, File.ReadAllBytes(files.Counter));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingFileOrDirectory_PreservedAndPersistenceFailureLatches(bool directory)
    {
        using var files = new Files(); files.Seed(20);
        string pending = files.Counter + ".pending";
        if (directory) Directory.CreateDirectory(pending); else File.WriteAllText(pending, "test-owned-unrelated-marker");
        await using var store = files.Open();
        Assert.Equal(Outcome.Unavailable, await store.TryReserveAsync()); Assert.Equal(20, files.Count());
        if (directory) Directory.Delete(pending); else { Assert.Equal("test-owned-unrelated-marker", File.ReadAllText(pending)); File.Delete(pending); }
        Assert.Equal(Outcome.Unavailable, await store.TryReserveAsync()); Assert.Equal(20, files.Count());
    }

    [Fact]
    public async Task SecondOwner_FailsClosedAndNeverDeletesStableLock()
    {
        using var files = new Files(); files.Seed(0);
        await using var second = files.Open();
        await using (var first = files.Open())
        {
            Assert.Equal(Outcome.Admitted, await first.TryReserveAsync());
            Assert.Equal(Outcome.Unavailable, await second.TryReserveAsync()); Assert.Equal(1, files.Count());
        }
        Assert.Equal(Outcome.Unavailable, await second.TryReserveAsync());
        Assert.True(File.Exists(files.Counter + ".lock"));
        await using var restarted = files.Open();
        Assert.Equal(Outcome.Admitted, await restarted.TryReserveAsync()); Assert.Equal(2, files.Count());
    }

    [Fact]
    public async Task PreCancellation_NoFilesAndDisposedStoreRejects()
    {
        using var files = new Files(); files.Seed(0);
        var store = files.Open();
        using var abort = new CancellationTokenSource(); abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TryReserveAsync(abort.Token));
        Assert.False(File.Exists(files.Counter + ".lock")); Assert.Equal(0, files.Count());
        await store.DisposeAsync(); await store.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.TryReserveAsync());
    }

    [Fact]
    public async Task UnreadableCounter_FailsClosedAndPreservesState()
    {
        using var files = new Files(); files.Seed(10);
        await using var store = files.Open();
        using (var blocked = new FileStream(files.Counter, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(Outcome.Unavailable, await store.TryReserveAsync());
        Assert.Equal(10, files.Count());
        Assert.Equal(Outcome.Unavailable, await store.TryReserveAsync());
    }

    [Fact]
    public async Task CancellationBeforeCommit_CleansOnlyOwnedPendingWithoutRefundOrDisable()
    {
        using var files = new Files(); files.Seed(10);
        await using var store = files.Open();
        using var abort = new CancellationTokenSource();
        files.Clock.OnRead = abort.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TryReserveAsync(abort.Token));
        Assert.Equal(10, files.Count()); Assert.False(File.Exists(files.Counter + ".pending"));
        Assert.Equal(Outcome.Admitted, await store.TryReserveAsync()); Assert.Equal(11, files.Count());
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotReserveAndActiveOwnerFinishesBeforeCleanup()
    {
        using var files = new Files(); files.Seed(0);
        await using var store = files.Open();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.Clock.OnRead = () =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test clock release deadline expired.");
        };
        // Only the controlled clock blocks; an explicit deadline and finally-release prevent a stranded test worker.
        var active = Task.Run(() => store.TryReserveAsync());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var abort = new CancellationTokenSource();
            var waiter = store.TryReserveAsync(abort.Token);
            Assert.False(waiter.IsCompleted); abort.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, files.Count());
        }
        finally { release.Set(); await active.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.Equal(Outcome.Admitted, await active); Assert.Equal(1, files.Count());
    }

    /// <summary>Provides an explicit UTC instant; no real timer or production clock is modified.</summary>
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        /// <summary>Runs one bounded test-owned action under the reservation gate for deterministic cancellation checks.</summary>
        internal Action? OnRead { get; set; }
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow()
        {
            var action = OnRead; OnRead = null;
            action?.Invoke(); return Now;
        }
    }

    /// <summary>Owns one unique temporary directory; counters are seeded explicitly to model reviewed operator provisioning.</summary>
    private sealed class Files : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "HealthMonitor-budget-" + Guid.NewGuid().ToString("N"));
        internal Clock Clock { get; } = new();
        internal string Counter => Path.Combine(root, "counter.json");
        /// <summary>Creates only the uniquely named test directory, never a runtime counter or shared directory.</summary>
        internal Files() => Directory.CreateDirectory(root);
        /// <summary>Creates a store owned and disposed by the individual test before directory cleanup.</summary>
        internal AiDailyBudgetStore Open() => new(Counter, Clock);
        /// <summary>Writes controlled state; tests explicitly own all state and markers in this unique directory.</summary>
        internal void Seed(int count, string day = "2026-10-18") => File.WriteAllText(Counter,
            JsonSerializer.Serialize(new { version = 1, day, count }), new UTF8Encoding(false));
        /// <summary>Reads the persisted count independently of the store's returned outcome.</summary>
        internal int Count() { using var json = JsonDocument.Parse(File.ReadAllText(Counter)); return json.RootElement.GetProperty("count").GetInt32(); }
        /// <summary>Reads the persisted UTC date independently of the store's clock.</summary>
        internal string? Day() { using var json = JsonDocument.Parse(File.ReadAllText(Counter)); return json.RootElement.GetProperty("day").GetString(); }
        /// <summary>Verifies the resolved target is this test's unique directory under the temporary root before recursive cleanup.</summary>
        public void Dispose()
        {
            string resolved = Path.GetFullPath(root);
            string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("HealthMonitor-budget-", StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path validation failed.");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
