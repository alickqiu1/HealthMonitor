using HealthMonitor.Application.IntegrationContracts;
using HealthMonitor.Application.Telemetry;
using HealthMonitor.Domain.Telemetry;

namespace HealthMonitor.UnitTests.Telemetry;

/// <summary>Verifies ingestion ordering, immutable inputs, replay, and failures without database access.</summary>
/// <remarks>Transaction doubles verify delegation only; they do not establish PostgreSQL atomicity or locking.</remarks>
public sealed class TelemetryIngestionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Provides a controlled server clock; tests can advance it across an asynchronous boundary.</summary>
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    /// <summary>Records port ordering and permits explicit failures without persistence or hidden retries.</summary>
    private sealed class Harness : ISimulationBatchRepository, IDeviceCatalog, ITelemetryIngestionTransaction
    {
        internal readonly List<string> Calls = new();
        internal readonly Clock Time = new();
        internal Func<Guid, CancellationToken, Task<SimulationBatch?>> Receipt { get; set; } =
            (_, _) => Task.FromResult<SimulationBatch?>(null);
        internal Func<IReadOnlyCollection<Guid>, CancellationToken, Task<IReadOnlyList<DeviceRegistration>>> Catalog { get; set; } =
            (ids, _) => Task.FromResult<IReadOnlyList<DeviceRegistration>>(ids.Select(id => Registration(id)).ToArray());
        internal Func<TelemetryIngestionBatch, CancellationToken, Task<TelemetryIngestionResult>> Write { get; set; } =
            (batch, _) => Task.FromResult(new TelemetryIngestionResult(batch.BatchId, batch.DeviceIds.Count, Now, false));
        internal TelemetryIngestionBatch? Batch { get; private set; }
        internal TelemetryIngestionService Service() => new(this, this, this, Time);

        public Task<SimulationBatch?> FindByIdAsync(Guid batchId, CancellationToken cancellationToken = default)
        {
            Calls.Add("receipt");
            return Receipt(batchId, cancellationToken);
        }

        public void Add(SimulationBatch receipt) => throw new InvalidOperationException("Application must not stage receipts.");

        public Task<IReadOnlyList<DeviceRegistration>> GetByIdsAsync(
            IReadOnlyCollection<Guid> deviceIds, CancellationToken cancellationToken = default)
        {
            Calls.Add("catalog");
            return Catalog(deviceIds, cancellationToken);
        }

        public Task<TelemetryIngestionResult> ExecuteAsync(TelemetryIngestionBatch batch, CancellationToken cancellationToken = default)
        {
            Calls.Add("transaction");
            Batch = batch;
            return Write(batch, cancellationToken);
        }
    }

    /// <summary>Creates valid requests; explicit invalid-field cases override these inputs directly.</summary>
    private static TelemetryIngestionRequest Request(string scenario = "Healthy", int count = 1) =>
        new(Guid.CreateVersion7(Now), scenario, Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray());

    /// <summary>Builds a catalog fact without depending on Device persistence or domain construction.</summary>
    private static DeviceRegistration Registration(Guid id, bool retired = false) =>
        new(id, "Synthetic device", "TEST", "DEN", "Server", retired);

    /// <summary>Creates a saved receipt with its own historical metadata for replay assertions.</summary>
    private static SimulationBatch ReceiptFor(TelemetryIngestionRequest request, string? hash = null) =>
        SimulationBatch.CreateCommitted(request.BatchId, hash ?? TelemetryRequestCanonicalizer.ComputeHash(request),
            request.Scenario, Now.AddDays(-10), Now.AddDays(-10).AddSeconds(1), request.DeviceIds.Count);

    [Fact]
    public void Constructor_NullDependencies_Throw()
    {
        var h = new Harness();
        Assert.Throws<ArgumentNullException>(() => new TelemetryIngestionService(null!, h, h, h.Time));
        Assert.Throws<ArgumentNullException>(() => new TelemetryIngestionService(h, null!, h, h.Time));
        Assert.Throws<ArgumentNullException>(() => new TelemetryIngestionService(h, h, null!, h.Time));
        Assert.Throws<ArgumentNullException>(() => new TelemetryIngestionService(h, h, h, null!));
    }

    [Theory]
    [InlineData("nullRequest")]
    [InlineData("nullIds")]
    [InlineData("emptyBatch")]
    [InlineData("emptyId")]
    [InlineData("duplicateId")]
    [InlineData("emptyIds")]
    [InlineData("tooMany")]
    [InlineData("unknownScenario")]
    [InlineData("nonCanonicalScenario")]
    public async Task InvalidShape_RejectsBeforeAnyPort(string kind)
    {
        var h = new Harness();
        var request = Request();
        request = kind switch
        {
            "nullRequest" => null!,
            "nullIds" => request with { DeviceIds = null! },
            "emptyBatch" => request with { BatchId = Guid.Empty },
            "emptyId" => request with { DeviceIds = new[] { Guid.Empty } },
            "duplicateId" => request with { DeviceIds = new[] { request.DeviceIds[0], request.DeviceIds[0] } },
            "emptyIds" => request with { DeviceIds = Array.Empty<Guid>() },
            "tooMany" => Request(count: 26),
            "unknownScenario" => request with { Scenario = "Other" },
            "nonCanonicalScenario" => request with { Scenario = " Healthy " },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => h.Service().IngestAsync(request));
        Assert.Empty(h.Calls);
    }

    [Theory]
    [InlineData("Healthy", 1)]
    [InlineData("HighLatency", 1)]
    [InlineData("HighCpu", 1)]
    [InlineData("Timeout", 1)]
    [InlineData("Healthy", 25)]
    public async Task ValidNewBatch_DelegatesExactStableInputsInOrder(string scenario, int count)
    {
        var h = new Harness();
        var request = Request(scenario, count);
        using var cancellation = new CancellationTokenSource();
        h.Receipt = (id, token) =>
        {
            Assert.Equal(request.BatchId, id);
            Assert.Equal(cancellation.Token, token);
            return Task.FromResult<SimulationBatch?>(null);
        };
        h.Catalog = (ids, token) =>
        {
            Assert.Equal(request.DeviceIds, ids);
            Assert.Equal(cancellation.Token, token);
            return Task.FromResult<IReadOnlyList<DeviceRegistration>>(ids.Select(id => Registration(id)).Reverse().ToArray());
        };
        var expected = new TelemetryIngestionResult(request.BatchId, count, Now.AddSeconds(1), true);
        h.Write = (batch, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            Assert.Equal(request.BatchId, batch.BatchId);
            Assert.Equal(request.Scenario, batch.Scenario);
            Assert.Equal(TelemetryRequestCanonicalizer.ComputeHash(request), batch.RequestHash);
            Assert.Equal(request.DeviceIds, batch.DeviceIds);
            Assert.Equal(Now, batch.RequestedAtUtc);
            Assert.Throws<NotSupportedException>(() => ((IList<Guid>)batch.DeviceIds)[0] = Guid.NewGuid());
            return Task.FromResult(expected); // A locked recheck may legitimately return a replay.
        };
        Assert.Same(expected, await h.Service().IngestAsync(request, cancellation.Token));
        Assert.Equal(new[] { "receipt", "catalog", "transaction" }, h.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingHistoricalReceipt_ReplaysWithoutFreshnessOrCatalog(bool nonVersion7)
    {
        var h = new Harness();
        var request = Request() with { BatchId = nonVersion7 ? Guid.NewGuid() : Guid.CreateVersion7(Now.AddDays(-10)) };
        var receipt = ReceiptFor(request);
        h.Receipt = (_, _) => Task.FromResult<SimulationBatch?>(receipt);
        var result = await h.Service().IngestAsync(request);
        Assert.Equal(new TelemetryIngestionResult(receipt.BatchId, receipt.AffectedDeviceCount, receipt.CommittedAtUtc, true), result);
        Assert.Equal(new[] { "receipt" }, h.Calls);
    }

    [Fact]
    public async Task ConflictingReceipt_RejectsBeforeCatalogOrFreshness()
    {
        var h = new Harness();
        var request = Request() with { BatchId = Guid.CreateVersion7(Now.AddDays(-10)) };
        h.Receipt = (_, _) => Task.FromResult<SimulationBatch?>(ReceiptFor(request, new string('b', 64)));
        await Assert.ThrowsAsync<TelemetryIngestionConflictException>(() => h.Service().IngestAsync(request));
        Assert.Equal(new[] { "receipt" }, h.Calls);
    }

    [Theory]
    [InlineData(-300000, true)]
    [InlineData(300000, true)]
    [InlineData(-300001, false)]
    [InlineData(300001, false)]
    public async Task NewBatch_FreshnessFollowsReceiptAndUsesExactBoundaries(int milliseconds, bool accepted)
    {
        var h = new Harness();
        var request = Request() with { BatchId = Guid.CreateVersion7(Now.AddMilliseconds(milliseconds)) };
        if (accepted) await h.Service().IngestAsync(request);
        else
        {
            var error = await Assert.ThrowsAsync<TelemetryBatchIdValidationException>(() => h.Service().IngestAsync(request));
            Assert.Equal(milliseconds < 0 ? "batch_id_expired" : "batch_id_future", error.Code);
        }
        Assert.Equal(accepted ? new[] { "receipt", "catalog", "transaction" } : new[] { "receipt" }, h.Calls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("retired")]
    [InlineData("mixed")]
    public async Task IneligibleDevice_RejectsEntireBatch(string kind)
    {
        var h = new Harness();
        var request = Request(count: 2);
        h.Catalog = (ids, _) => Task.FromResult<IReadOnlyList<DeviceRegistration>>(kind switch
        {
            "missing" => Array.Empty<DeviceRegistration>(),
            "retired" => ids.Select(id => Registration(id, true)).ToArray(),
            "mixed" => new[] { Registration(request.DeviceIds[0]), Registration(request.DeviceIds[1], true) },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        });
        await Assert.ThrowsAsync<TelemetryDeviceEligibilityException>(() => h.Service().IngestAsync(request));
        Assert.Equal(new[] { "receipt", "catalog" }, h.Calls);
        Assert.Null(h.Batch);
    }

    [Fact]
    public async Task InputMutationDuringReceiptLookup_DoesNotChangeHashCatalogOrBatch()
    {
        var h = new Harness();
        var original = Request();
        var ids = original.DeviceIds.ToList();
        var request = original with { DeviceIds = ids };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<SimulationBatch?>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Receipt = (_, _) => { entered.SetResult(); return release.Task; };
        var operation = h.Service().IngestAsync(request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ids[0] = Guid.NewGuid();
        h.Time.UtcNow = Now.AddSeconds(1);
        release.SetResult(null);
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(original.DeviceIds, h.Batch!.DeviceIds);
        Assert.Equal(TelemetryRequestCanonicalizer.ComputeHash(original), h.Batch.RequestHash);
        Assert.Equal(Now, h.Batch.RequestedAtUtc);
    }

    [Fact]
    public async Task ConcurrentCommitDuringFailedEligibility_RejectsThenSameIdRetryReplays()
    {
        var h = new Harness();
        var request = Request();
        SimulationBatch? saved = null;
        h.Receipt = (_, _) => Task.FromResult(saved);
        h.Catalog = (_, _) =>
        {
            // Another writer's commit is simulated between the prescribed two lookup positions.
            saved = ReceiptFor(request);
            return Task.FromResult<IReadOnlyList<DeviceRegistration>>(Array.Empty<DeviceRegistration>());
        };
        await Assert.ThrowsAsync<TelemetryDeviceEligibilityException>(() => h.Service().IngestAsync(request));
        Assert.Equal(new[] { "receipt", "catalog" }, h.Calls);
        Assert.True((await h.Service().IngestAsync(request)).Replayed);
        Assert.Equal(new[] { "receipt", "catalog", "receipt" }, h.Calls);
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("catalog")]
    [InlineData("transaction")]
    public async Task DependencyFailure_PropagatesWithoutRetry(string phase)
    {
        var h = new Harness();
        var failure = new InvalidOperationException("Synthetic dependency failure.");
        if (phase == "receipt") h.Receipt = (_, _) => Task.FromException<SimulationBatch?>(failure);
        if (phase == "catalog") h.Catalog = (_, _) => Task.FromException<IReadOnlyList<DeviceRegistration>>(failure);
        if (phase == "transaction") h.Write = (_, _) => Task.FromException<TelemetryIngestionResult>(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service().IngestAsync(Request())));
        Assert.Equal(phase == "receipt" ? 1 : phase == "catalog" ? 2 : 3, h.Calls.Count);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("receipt")]
    [InlineData("catalog")]
    [InlineData("transaction")]
    public async Task Cancellation_StopsSubsequentWorkAndPreservesToken(string phase)
    {
        var h = new Harness();
        using var cancellation = new CancellationTokenSource();
        if (phase == "before") cancellation.Cancel();
        h.Receipt = (_, _) =>
        {
            if (phase == "receipt") cancellation.Cancel();
            return Task.FromResult<SimulationBatch?>(null);
        };
        h.Catalog = (ids, _) =>
        {
            if (phase == "catalog") cancellation.Cancel();
            return Task.FromResult<IReadOnlyList<DeviceRegistration>>(ids.Select(id => Registration(id)).ToArray());
        };
        h.Write = (_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<TelemetryIngestionResult>(token);
        };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Service().IngestAsync(Request(), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(phase == "before" ? 0 : phase == "receipt" ? 1 : phase == "catalog" ? 2 : 3, h.Calls.Count);
    }
}
