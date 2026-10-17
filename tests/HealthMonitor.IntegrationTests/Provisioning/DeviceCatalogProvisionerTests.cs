using HealthMonitor.Api.Provisioning;
using HealthMonitor.IntegrationTests.Infrastructure;
using static HealthMonitor.IntegrationTests.Provisioning.DeviceProvisioningTestScenario;

namespace HealthMonitor.IntegrationTests.Provisioning;

/// <summary>Verifies committed provisioning, rejection-state preservation, capacity, and concurrent calls.</summary>
/// <remarks>
/// Requires verified healthmonitor_test and exclusive use by one test process. Initialized
/// scenarios clean only owned rows after completed operations, including assertion failures.
/// Unresponsive writers cause fail-stop termination without cleanup; discard that disposable
/// database rather than adopting unknown leftovers. Baseline rows and schemas are preserved.
/// Conflicts and capacity failures happen before insertion: these tests do not establish rollback
/// after partial insertion. Deterministic mid-insert failure injection remains explicitly deferred.
/// </remarks>
[Collection(DeviceDatabaseCollection.Name)]
public sealed class DeviceCatalogProvisionerTests
{
    [Fact]
    public Task EmptyCatalog_InsertsAll24ExpectedDevices() => RunAsync(async scenario =>
    {
        // An occupied database is not permission to erase records to manufacture this condition.
        Assert.Empty(scenario.Baseline);
        Assert.Equal(24, await scenario.CreateProvisioner().ProvisionAsync(scenario.Token));
        await AssertCatalogAsync(scenario, 24);
    });

    [Fact]
    public Task RepeatedProvisioning_ReturnsZeroAndPreservesRows() => RunAsync(async scenario =>
    {
        var provisioner = scenario.CreateProvisioner();
        Assert.Equal(24, await provisioner.ProvisionAsync(scenario.Token));
        var first = await scenario.SnapshotAsync();
        Assert.Equal(0, await provisioner.ProvisionAsync(scenario.Token));
        Assert.Equal(first, await scenario.SnapshotAsync()); // Includes creation and retirement timestamps.
        await AssertCatalogAsync(scenario, scenario.Baseline.Length + 24);
    });

    [Fact]
    public Task PartialMatchingCatalog_InsertsOnlyMissingDevices() => RunAsync(async scenario =>
    {
        var existing = FixedDeviceCatalog.Devices.Take(3).Select(SeedRow).ToArray();
        await scenario.InsertAsync(existing);
        Assert.Equal(21, await scenario.CreateProvisioner().ProvisionAsync(scenario.Token));
        var result = await scenario.SnapshotAsync();
        foreach (var row in existing) Assert.Contains(row, result);
        await AssertCatalogAsync(scenario, scenario.Baseline.Length + 24);
    });

    [Fact]
    public Task FirstRun_PreservesUnrelatedRows() => RunAsync(async scenario =>
    {
        var sentinel = ExtraRow(retired: true);
        await scenario.InsertAsync(sentinel);
        var before = await scenario.SnapshotAsync();
        Assert.Equal(24, await scenario.CreateProvisioner().ProvisionAsync(scenario.Token));
        var after = await scenario.SnapshotAsync();
        foreach (var row in before) Assert.Contains(row, after);
        await AssertCatalogAsync(scenario, before.Length + 24);
    });

    [Fact]
    public Task ConflictingId_RejectsWithoutChangingState() => RunAsync(async scenario =>
    {
        var conflict = SeedRow(FixedDeviceCatalog.Devices.Last()) with { AssetTag = $"CONFLICT-{Guid.NewGuid():N}" };
        await scenario.InsertAsync(conflict);
        await AssertRejectedUnchangedAsync(scenario, "Conflicting device ID");
    });

    [Fact]
    public Task ConflictingAssetTag_RejectsWithoutChangingState() => RunAsync(async scenario =>
    {
        var conflict = SeedRow(FixedDeviceCatalog.Devices.Last()) with { Id = Guid.NewGuid() };
        await scenario.InsertAsync(conflict);
        await AssertRejectedUnchangedAsync(scenario, "Conflicting asset tag");
    });

    [Theory]
    [InlineData("displayName")]
    [InlineData("siteCode")]
    [InlineData("deviceType")]
    public Task ConflictingMetadata_RejectsWithoutChangingState(string field) => RunAsync(async scenario =>
    {
        var row = SeedRow(FixedDeviceCatalog.Devices.Last());
        row = field switch
        {
            "displayName" => row with { DisplayName = "Different inventory name" },
            "siteCode" => row with { SiteCode = "EDM" },
            "deviceType" => row with { DeviceType = "Server" },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        await scenario.InsertAsync(row);
        await AssertRejectedUnchangedAsync(scenario, "Conflicting device ID");
    });

    [Fact]
    public Task RetiredSeed_RejectsWithoutReactivation() => RunAsync(async scenario =>
    {
        var row = SeedRow(FixedDeviceCatalog.Devices.Last());
        await scenario.InsertAsync(row with { RetiredAtUtc = row.CreatedAtUtc.AddDays(1) });
        await AssertRejectedUnchangedAsync(scenario, "Conflicting device ID");
    });

    /// <summary>Checks the active-fleet boundary with retired history and repeat provisioning.</summary>
    /// <param name="projectedCount">Active registrations expected after inserting all missing fixed seeds.</param>
    /// <returns>Completion after the scenario verifies committed state or unchanged rejection and performs owned cleanup.</returns>
    /// <remarks>Raw row totals intentionally exceed the active limit; retired rows must not consume fleet capacity.</remarks>
    [Theory]
    [InlineData(1000)]
    [InlineData(1001)]
    public Task Capacity_ExactLimitSucceeds_OverflowRejects(int projectedCount) => RunAsync(async scenario =>
    {
        // The scenario rejects pre-existing fixed seeds, so all 24 start missing.
        // Size only active fillers; retain baseline history rather than deleting rows for room.
        int baselineActiveCount = scenario.Baseline.Count(row => !row.RetiredAtUtc.HasValue);
        int activeFillerCount = projectedCount - 24 - baselineActiveCount;
        Assert.True(activeFillerCount >= 0, "Capacity setup cannot remove baseline data to make room for the seed.");
        var retired = ExtraRow(retired: true);
        // InsertAsync constructs domain entities, so use canonical tags for exact snapshot comparisons.
        var fillers = Enumerable.Range(0, activeFillerCount).Select(_ => ExtraRow()).Append(retired)
            .Select(row => row with { AssetTag = row.AssetTag.ToUpperInvariant() }).ToArray();
        await scenario.InsertAsync(fillers);
        if (projectedCount == 1001)
            await AssertRejectedUnchangedAsync(scenario, "catalog limit");
        else
        {
            var provisioner = scenario.CreateProvisioner();
            Assert.Equal(24, await provisioner.ProvisionAsync(scenario.Token));
            await AssertCatalogAsync(scenario, scenario.Baseline.Length + activeFillerCount + 1 + 24);
            var first = await scenario.SnapshotAsync();
            Assert.Equal(1000, first.Count(row => !row.RetiredAtUtc.HasValue));
            Assert.True(first.Length > 1000, "Retired history must not consume active fleet capacity.");
            Assert.All(fillers, row => Assert.Contains(row, first));
            Assert.Equal(0, await provisioner.ProvisionAsync(scenario.Token));
            Assert.Equal(first, await scenario.SnapshotAsync());
        }
    });

    [Fact]
    public Task ConcurrentProvisioning_InsertsOnce() => RunAsync(async scenario =>
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(scenario.Token);
        await using var blocker = await scenario.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync(scenario.Token);
        await using (var command = new Npgsql.NpgsqlCommand(
            "LOCK TABLE device.monitored_devices IN SHARE ROW EXCLUSIVE MODE", blocker, transaction))
            await command.ExecuteNonQueryAsync(scenario.Token);

        Task<int> first = scenario.StartProvisioningAsync(cancellation.Token);
        Task<int> second = scenario.StartProvisioningAsync(cancellation.Token);
        var failures = new List<Exception>();
        try
        {
            // Observe two distinct PostgreSQL sessions queued behind our harmless lock.
            // A start gate alone would not prove the calls actually overlapped.
            await using var waiting = new Npgsql.NpgsqlCommand("""
                SELECT count(DISTINCT pid) FROM pg_locks
                WHERE relation = 'device.monitored_devices'::regclass
                  AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                  AND mode = 'ShareRowExclusiveLock' AND NOT granted
                  AND pid <> pg_backend_pid()
                """, blocker, transaction);
            while (Convert.ToInt64(await waiting.ExecuteScalarAsync(scenario.Token)) < 2)
                await Task.Delay(25, scenario.Token);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
            cancellation.Cancel();
        }
        // Release the barrier on every path. A fresh deadline allows release after body cancellation.
        using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await transaction.RollbackAsync(releaseTimeout.Token); }
        catch (Exception release)
        {
            failures.Add(release);
            cancellation.Cancel();
            try { await blocker.CloseAsync(); }
            catch (Exception close) { failures.Add(close); }
        }
        // Observe both workers; RunAsync enforces an outer deadline and independently drains registered writers.
        Task<int[]> workers = Task.WhenAll(first, second);
        int[] results = Array.Empty<int>();
        try { results = await workers; }
        catch (Exception exception) { failures.Add(workers.Exception ?? exception); }
        if (failures.Count != 0)
            throw new AggregateException("Concurrent provisioning verification failed.", failures);
        Assert.Equal(new[] { 0, 24 }, results.Order().ToArray());
        await AssertCatalogAsync(scenario, scenario.Baseline.Length + 24);
    });

    /// <summary>Checks fixed registrations and pre-existing baseline preservation in a committed snapshot.</summary>
    /// <param name="scenario">Initialized scenario owning only this test's inserted identities.</param>
    /// <param name="expectedCount">Total stored rows, including retired history; not the active-capacity count.</param>
    /// <returns>Completion after all fixed rows, baseline rows, and the total count have been checked.</returns>
    private static async Task AssertCatalogAsync(DeviceProvisioningTestScenario scenario, int expectedCount)
    {
        var rows = await scenario.SnapshotAsync();
        Assert.Equal(expectedCount, rows.Length);
        foreach (var baseline in scenario.Baseline) Assert.Contains(baseline, rows);
        foreach (var seed in FixedDeviceCatalog.Devices)
        {
            var row = Assert.Single(rows, row => row.Id == seed.Id);
            Assert.Equal(seed.AssetTag, row.AssetTag);
            Assert.Equal(seed.DisplayName, row.DisplayName);
            Assert.Equal(seed.DeviceType, row.DeviceType);
            Assert.Equal(seed.SiteCode, row.SiteCode);
            Assert.Null(row.RetiredAtUtc);
            Assert.NotEqual(default, row.CreatedAtUtc);
            Assert.Equal(TimeSpan.Zero, row.CreatedAtUtc.Offset);
        }
    }

    /// <summary>Asserts a specific pre-insert rejection and complete committed-state preservation.</summary>
    /// <param name="scenario">Initialized scenario with committed conflict or capacity setup.</param>
    /// <param name="message">Expected diagnostic fragment distinguishing the intended rejection path.</param>
    /// <returns>Completion after comparing full committed snapshots before and after rejection.</returns>
    private static async Task AssertRejectedUnchangedAsync(DeviceProvisioningTestScenario scenario, string message)
    {
        var before = await scenario.SnapshotAsync();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scenario.CreateProvisioner().ProvisionAsync(scenario.Token));
        Assert.Contains(message, exception.Message);
        Assert.Equal(before, await scenario.SnapshotAsync());
    }
}
