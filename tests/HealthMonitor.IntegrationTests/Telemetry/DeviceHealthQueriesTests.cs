using System.Data.Common;
using HealthMonitor.Application.IntegrationContracts;
using HealthMonitor.Domain.Telemetry;
using HealthMonitor.Infrastructure.Data.TelemetryContext;
using HealthMonitor.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HealthMonitor.IntegrationTests.Telemetry;

/// <summary>Verifies real latest-state projection and input isolation using only fixture-owned telemetry identities.</summary>
[Collection(TelemetryIngestionDatabaseCollection.Name)]
public sealed class DeviceHealthQueriesTests
{
    /// <summary>Seeds through the verified ingestion workflow; every receipt is reserved by the scenario before writing.</summary>
    private static async Task WriteAsync(TelemetryIngestionTestScenario test, string scenario, IReadOnlyList<Guid> ids)
    {
        var request = (await test.RequestAsync(scenario)) with { DeviceIds = Array.AsReadOnly(ids.ToArray()) };
        await using var context = test.Context();
        await test.Start(test.Service(context), request);
    }

    /// <summary>Gates/fails the actual EF reader path after caller IDs must already have been snapshotted.</summary>
    private sealed class ReadProbe(Func<CancellationToken, Task> beforeRead) : DbCommandInterceptor
    {
        /// <inheritdoc />
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("device_telemetry_state", StringComparison.Ordinal)) await beforeRead(cancellationToken);
            return result;
        }
    }

    [Fact]
    public Task NoStates_ReturnsEmptyAndUntracked() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        await using var context = test.Context();
        var rows = await new DeviceHealthQueries(context).GetLatestByDeviceIdsAsync(test.DeviceIds.Append(Guid.Empty).ToArray(), test.Token);
        Assert.Empty(rows);
        Assert.Empty(context.ChangeTracker.Entries());
    });

    [Fact]
    public Task SelectedStates_ProjectsOrderedDistinctSnapshotsAndPreservesLastSuccess() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        var firstTime = test.UtcNow;
        await WriteAsync(test, "Timeout", new[] { test.DeviceIds[0] });
        await WriteAsync(test, "Healthy", new[] { test.DeviceIds[1] });
        await using (var initial = test.Context())
        {
            var rows = await new DeviceHealthQueries(initial).GetLatestByDeviceIdsAsync(test.DeviceIds, test.Token);
            Assert.Equal(new DeviceTelemetrySnapshot(test.DeviceIds[0], firstTime, null, PingOutcome.Timeout, null, null),
                rows.Single(row => row.DeviceId == test.DeviceIds[0]));
            Assert.Equal(new DeviceTelemetrySnapshot(test.DeviceIds[1], firstTime, firstTime, PingOutcome.Success, 40, 30m),
                rows.Single(row => row.DeviceId == test.DeviceIds[1]));
            Assert.Empty(initial.ChangeTracker.Entries());
        }
        test.UtcNow += TimeSpan.FromSeconds(1);
        await WriteAsync(test, "Healthy", new[] { test.DeviceIds[0] });
        await WriteAsync(test, "Timeout", new[] { test.DeviceIds[1] });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new ReadProbe(async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); });
        var context = test.Context(probe);
        Task<IReadOnlyList<DeviceTelemetrySnapshot>>? query = null;
        try
        {
            var ids = new List<Guid> { test.DeviceIds[1], Guid.NewGuid(), test.DeviceIds[0], test.DeviceIds[1], Guid.Empty };
            query = test.TrackWriter(new DeviceHealthQueries(context).GetLatestByDeviceIdsAsync(ids, test.Token));
            await Task.WhenAny(entered.Task, query).WaitAsync(test.Token);
            if (!entered.Task.IsCompleted) await query;
            await entered.Task.WaitAsync(test.Token);
            ids.Clear();
            ids.Add(Guid.Empty);
            release.TrySetResult();
            var rows = await query;
            var expected = new[]
            {
                new DeviceTelemetrySnapshot(test.DeviceIds[0], test.UtcNow, test.UtcNow, PingOutcome.Success, 40, 30m),
                new DeviceTelemetrySnapshot(test.DeviceIds[1], test.UtcNow, firstTime, PingOutcome.Timeout, null, null)
            }.OrderBy(row => row.DeviceId);
            Assert.Equal(expected, rows);
            Assert.Empty(context.ChangeTracker.Entries());
        }
        finally
        {
            release.TrySetResult();
            if (query is not null) await TelemetryIngestionTestScenario.DrainWritersAsync(query);
            await context.DisposeAsync();
        }
    });

    [Fact]
    public Task ExactCapacityAndDuplicateInputs_AcceptsThousandDistinctAndCollapsesDuplicates() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        await WriteAsync(test, "Healthy", test.DeviceIds);
        var ids = new HashSet<Guid>(test.DeviceIds);
        while (ids.Count < 1000) ids.Add(Guid.NewGuid());
        await using var context = test.Context();
        var queries = new DeviceHealthQueries(context);
        Assert.Equal(test.DeviceIds.Order(), (await queries.GetLatestByDeviceIdsAsync(ids, test.Token)).Select(row => row.DeviceId));
        var repeated = Enumerable.Repeat(test.DeviceIds[0], 1001).ToArray();
        Assert.Equal(test.DeviceIds[0], Assert.Single(await queries.GetLatestByDeviceIdsAsync(repeated, test.Token)).DeviceId);
        Assert.Empty(context.ChangeTracker.Entries());
    });

    [Fact]
    public Task QueryFailure_PropagatesWithoutEmptyFallback() => TelemetryIngestionTestScenario.RunAsync(async test =>
    {
        var error = new InvalidOperationException("Injected read failure");
        await using var context = test.Context(new ReadProbe(_ => throw error));
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DeviceHealthQueries(context).GetLatestByDeviceIdsAsync(test.DeviceIds, test.Token)));
        Assert.Empty(context.ChangeTracker.Entries());
    });
}
