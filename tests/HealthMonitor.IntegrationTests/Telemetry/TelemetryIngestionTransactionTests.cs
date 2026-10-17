using System.Data.Common;
using HealthMonitor.Application.Telemetry;
using HealthMonitor.Domain.Telemetry;
using HealthMonitor.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace HealthMonitor.IntegrationTests.Telemetry;

/// <summary>Verifies real atomic persistence and replay using exclusively owned disposable PostgreSQL data.</summary>
/// <remarks>
/// Device facts are supplied through a port double; Device query SQL has its own integration suite.
/// No migrations or hosted connections occur here. Failure interception is test-only and does not
/// demonstrate actual network loss, although independent reads establish the real committed outcome.
/// </remarks>
[Collection(TelemetryIngestionDatabaseCollection.Name)]
public sealed class TelemetryIngestionTransactionTests
{
    /// <summary>Fails after SaveChanges has executed its SQL but before the explicit transaction commits.</summary>
    private sealed class FailAfterSave(Exception? failure = null) : SaveChangesInterceptor
    {
        internal bool Executed { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            Executed = true;
            throw failure ?? new InvalidOperationException("Injected after executed SaveChanges.");
        }
    }

    /// <summary>Simulates an exception after real commit acknowledgement; it does not interrupt a network connection.</summary>
    private sealed class FailAfterCommit : DbTransactionInterceptor
    {
        internal bool Committed { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Committed = true;
            throw new InvalidOperationException("Injected after real transaction commit.");
        }
    }

    /// <summary>Replaces only test context transaction creation; production has no failure switch.</summary>
    public sealed class CleanupFailureTransactionFactory(RelationalTransactionFactoryDependencies dependencies)
        : RelationalTransactionFactory(dependencies)
    {
        public override RelationalTransaction Create(IRelationalConnection connection, DbTransaction transaction, Guid transactionId,
            IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger, bool transactionOwned) =>
            new CleanupFailureTransaction(connection, transaction, transactionId, logger, transactionOwned, Dependencies.SqlGenerationHelper);
    }

    /// <summary>Throws only after actual disposal has completed, without leaving a live test writer or lock.</summary>
    private sealed class CleanupFailureTransaction(IRelationalConnection connection, DbTransaction transaction, Guid transactionId,
        IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger, bool transactionOwned, ISqlGenerationHelper sql)
        : RelationalTransaction(connection, transaction, transactionId, logger, transactionOwned, sql)
    {
        private bool _disposed;
        public override async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await base.DisposeAsync();
            throw new InvalidOperationException("Injected after transaction disposal.");
        }
    }

    [Fact]
    public Task ConfirmedCommit_CleanupFailureStillReturnsSavedResult() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        var request = await test.RequestAsync();
        await using var context = test.CleanupFailureContext();
        var result = await test.Start(test.Service(context), request);
        Assert.False(result.Replayed);
        await using var read = test.Context();
        Assert.Equal(result.CommittedAtUtc, (await read.SimulationBatches.SingleAsync(batch => batch.BatchId == request.BatchId, test.Token)).CommittedAtUtc);
    });

    [Theory]
    [InlineData("conflict")]
    [InlineData("busy")]
    [InlineData("cancellation")]
    public Task PrimaryFailureType_SurvivesCleanupFailure(string kind) => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        var request = await test.RequestAsync("HighCpu");
        Exception failure = kind switch
        {
            "conflict" => new TelemetryIngestionConflictException(),
            "busy" => new TelemetryIngestionBusyException(),
            _ => new OperationCanceledException(test.Token)
        };
        await using var context = test.CleanupFailureContext(new FailAfterSave(failure));
        var actual = await Record.ExceptionAsync(() => test.Start(test.Service(context), request));
        Assert.Same(failure, actual);
        await AssertAbsentAsync(test, request.BatchId);
    });
    [Theory]
    [InlineData("Healthy")]
    [InlineData("HighLatency")]
    [InlineData("HighCpu")]
    [InlineData("Timeout")]
    public Task NewBatch_PersistsCompleteAtomicResult(string scenario) => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        var request = await test.RequestAsync(scenario);
        await using var context = test.Context();
        var result = await test.Start(test.Service(context), request);
        Assert.False(result.Replayed);
        Assert.Equal(2, result.AffectedDeviceCount);
        await using var read = test.Context();
        var receipt = await read.SimulationBatches.SingleAsync(batch => batch.BatchId == request.BatchId, test.Token);
        var pings = await read.TelemetryPings.Where(ping => ping.BatchId == request.BatchId).ToArrayAsync(test.Token);
        var states = await read.DeviceTelemetryStates.Where(state => test.DeviceIds.Contains(state.DeviceId)).ToArrayAsync(test.Token);
        Assert.Equal(result.CommittedAtUtc, receipt.CommittedAtUtc);
        Assert.Equal(test.UtcNow, receipt.RequestedAtUtc);
        Assert.Equal(2, pings.Length);
        Assert.Equal(2, states.Length);
        Assert.All(pings, ping => Assert.Equal(test.UtcNow, ping.ObservedAtUtc));
        Assert.All(states, state => Assert.Contains(pings, ping => ping.Id == state.LastPingId && ping.DeviceId == state.DeviceId));
        Assert.Equal(scenario == "Healthy" ? 0 : 2, await read.IncidentLogs.CountAsync(incident => test.DeviceIds.Contains(incident.DeviceId), test.Token));
        if (scenario == "Timeout") Assert.All(states, state => Assert.Null(state.LastSuccessfulAtUtc));
    });

    [Fact]
    public Task MatchingReplay_AfterRetirementAndExpiry_PreservesSavedResult() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        var request = await test.RequestAsync("HighCpu");
        TelemetryIngestionResult first;
        await using (var context = test.Context()) first = await test.Start(test.Service(context), request);
        test.UtcNow = test.UtcNow.AddDays(8);
        await using (var context = test.Context())
        {
            var replay = await test.Start(test.Service(context, new() { Retired = true }), request with { DeviceIds = request.DeviceIds.Reverse().ToArray() });
            Assert.Equal(first with { Replayed = true }, replay);
        }
        await using var read = test.Context();
        Assert.Equal(2, await read.TelemetryPings.CountAsync(ping => ping.BatchId == request.BatchId, test.Token));
        Assert.Equal(2, await read.IncidentLogs.CountAsync(incident => test.DeviceIds.Contains(incident.DeviceId), test.Token));
    });

    [Fact]
    public Task ConflictingReplay_DoesNotWrite() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        var request = await test.RequestAsync();
        await using (var context = test.Context()) await test.Start(test.Service(context), request);
        await using (var context = test.Context())
            await Assert.ThrowsAsync<TelemetryIngestionConflictException>(() => test.Start(test.Service(context), request with { Scenario = "Timeout" }));
        await using var read = test.Context();
        Assert.Equal(2, await read.TelemetryPings.CountAsync(ping => ping.BatchId == request.BatchId, test.Token));
        Assert.Empty(await read.IncidentLogs.Where(incident => test.DeviceIds.Contains(incident.DeviceId)).ToArrayAsync(test.Token));
    });

    [Theory]
    [InlineData("missing")]
    [InlineData("retired")]
    public Task IneligibleNewBatch_RejectsWithoutPersistence(string registrationState) => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        var request = await test.RequestAsync();
        await using var context = test.Context();
        await Assert.ThrowsAsync<TelemetryDeviceEligibilityException>(() => test.Start(
            test.Service(context, new() { Retired = registrationState == "retired", Missing = registrationState == "missing" }), request));
        await AssertAbsentAsync(test, request.BatchId);
    });

    [Fact]
    public Task FailureAfterExecutedWrites_RollsBackAndInvalidatesContext() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        var request = await test.RequestAsync("HighCpu");
        var failure = new FailAfterSave();
        await using var context = test.Context(failure);
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Start(test.Service(context), request));
        Assert.True(failure.Executed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => context.SaveChangesAsync());
        await AssertAbsentAsync(test, request.BatchId);
    });

    [Fact]
    public Task ExceptionAfterRealCommit_SameIdRetryReplays() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        var request = await test.RequestAsync("Timeout");
        var failure = new FailAfterCommit();
        await using (var context = test.Context(failure))
            await Assert.ThrowsAsync<InvalidOperationException>(() => test.Start(test.Service(context), request));
        Assert.True(failure.Committed);
        await using var fresh = test.Context();
        Assert.True((await test.Start(test.Service(fresh), request)).Replayed);
        Assert.Equal(2, await fresh.TelemetryPings.CountAsync(ping => ping.BatchId == request.BatchId, test.Token));
    });

    [Theory]
    [InlineData("equal")]
    [InlineData("regressing")]
    public Task EqualObservationAccepted_RegressingObservationRejects(string chronology) => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        var first = await test.RequestAsync("HighCpu");
        await using (var context = test.Context()) await test.Start(test.Service(context), first);
        bool regress = chronology == "regressing";
        if (regress) test.UtcNow = test.UtcNow.AddMilliseconds(-1);
        var next = await test.RequestAsync("Healthy");
        await using (var context = test.Context())
        {
            if (regress) await Assert.ThrowsAsync<InvalidOperationException>(() => test.Start(test.Service(context), next));
            else await test.Start(test.Service(context), next);
        }
        await using var read = test.Context();
        Assert.Equal(regress ? 0 : 2, await read.TelemetryPings.CountAsync(ping => ping.BatchId == next.BatchId, test.Token));
        Assert.Equal(regress ? 2 : 0, await read.IncidentLogs.CountAsync(incident => test.DeviceIds.Contains(incident.DeviceId) && incident.ResolvedAtUtc == null, test.Token));
    });

    [Fact]
    public Task TimeoutPreservesMeasurementIncidents_HealthyResolvesAll() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        foreach (string scenario in new[] { "HighLatency", "HighCpu", "Timeout", "Healthy" })
        {
            var request = await test.RequestAsync(scenario);
            await using var context = test.Context();
            await test.Start(test.Service(context), request);
            await using var read = test.Context();
            int open = await read.IncidentLogs.CountAsync(incident => test.DeviceIds.Contains(incident.DeviceId) && incident.ResolvedAtUtc == null, test.Token);
            // HighCpu resolves Latency; timeout preserves Cpu and opens Connectivity.
            Assert.Equal(scenario == "Timeout" ? 4 : scenario == "Healthy" ? 0 : 2, open);
            test.UtcNow = test.UtcNow.AddSeconds(1);
        }
    });

    [Theory]
    [InlineData("context")]
    [InlineData("enlisted")]
    public Task CallerTransaction_IsRejectedAndRemainsOwned(string transactionKind) => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        var request = await test.RequestAsync();
        await using var context = test.Context();
        if (transactionKind == "enlisted")
        {
            using var enlisted = new System.Transactions.CommittableTransaction();
            await context.Database.OpenConnectionAsync(test.Token);
            System.Transactions.TransactionsDatabaseFacadeExtensions.EnlistTransaction(context.Database, enlisted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => test.Start(test.Service(context), request));
            Assert.Same(enlisted, System.Transactions.TransactionsDatabaseFacadeExtensions.GetEnlistedTransaction(context.Database));
            enlisted.Rollback();
        }
        else
        {
            await using var existing = await context.Database.BeginTransactionAsync(test.Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => test.Start(test.Service(context), request));
            Assert.Same(existing, context.Database.CurrentTransaction);
            await existing.RollbackAsync(test.Token);
        }
        // Access still works: pre-admission rejection must not dispose caller-owned resources.
        Assert.False(await context.SimulationBatches.AnyAsync(batch => batch.BatchId == request.BatchId, test.Token));
    });

    [Fact]
    public Task OneWriteAttempt_RejectsSecondBatchWithoutChangingFirst() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        var first = await test.RequestAsync();
        var second = await test.RequestAsync("HighCpu");
        await using var context = test.Context();
        var service = test.Service(context);
        await test.Start(service, first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Start(service, second));
        await using var read = test.Context();
        Assert.True(await read.SimulationBatches.AnyAsync(batch => batch.BatchId == first.BatchId, test.Token));
        Assert.False(await read.SimulationBatches.AnyAsync(batch => batch.BatchId == second.BatchId, test.Token));
        Assert.Equal(2, await read.TelemetryPings.CountAsync(ping => ping.BatchId == first.BatchId, test.Token));
    });

    [Fact]
    public Task RepeatedViolation_PreservesOriginalIncidentIdentityAndOpening() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        var request = await test.RequestAsync("HighCpu");
        await using (var context = test.Context()) await test.Start(test.Service(context), request);
        await using var baseline = test.Context();
        var original = await baseline.IncidentLogs.AsNoTracking().Where(incident => test.DeviceIds.Contains(incident.DeviceId))
            .OrderBy(incident => incident.Id).ToArrayAsync(test.Token);
        test.UtcNow = test.UtcNow.AddSeconds(1);
        var next = await test.RequestAsync("HighCpu");
        await using (var context = test.Context()) await test.Start(test.Service(context), next);
        var after = await baseline.IncidentLogs.AsNoTracking().Where(incident => test.DeviceIds.Contains(incident.DeviceId))
            .OrderBy(incident => incident.Id).ToArrayAsync(test.Token);
        Assert.Equal(original.Select(incident => (incident.Id, incident.OpenedAtUtc, incident.OpenedByPingId)),
            after.Select(incident => (incident.Id, incident.OpenedAtUtc, incident.OpenedByPingId)));
        Assert.All(after, incident => Assert.Null(incident.ResolvedAtUtc));
    });

    [Theory]
    [InlineData("expired")]
    [InlineData("future")]
    public Task MissingReceiptOutsideFreshnessWindow_RejectsWithoutPersistence(string direction) => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        var request = await test.RequestAsync();
        test.UtcNow = test.UtcNow.AddMinutes(direction == "expired" ? 6 : -6);
        await using var context = test.Context();
        var error = await Assert.ThrowsAsync<TelemetryBatchIdValidationException>(() => test.Start(test.Service(context), request));
        Assert.Equal(direction == "expired" ? "batch_id_expired" : "batch_id_future", error.Code);
        await AssertAbsentAsync(test, request.BatchId);
    });
    /// <summary>Uses a fresh context/connection to establish that no part of a rejected batch persisted.</summary>
    private static async Task AssertAbsentAsync(TelemetryIngestionTestScenario test, Guid batchId)
    {
        await using var read = test.Context();
        Assert.False(await read.SimulationBatches.AnyAsync(batch => batch.BatchId == batchId, test.Token));
        Assert.False(await read.TelemetryPings.AnyAsync(ping => ping.BatchId == batchId, test.Token));
        Assert.False(await read.DeviceTelemetryStates.AnyAsync(state => test.DeviceIds.Contains(state.DeviceId), test.Token));
        Assert.False(await read.IncidentLogs.AnyAsync(incident => test.DeviceIds.Contains(incident.DeviceId), test.Token));
    }
}
