using HealthMonitor.Api.Fleet;
using HealthMonitor.Application.Device;
using HealthMonitor.Application.IntegrationContracts;
using HealthMonitor.Application.Telemetry;
using HealthMonitor.Domain.Telemetry;

namespace HealthMonitor.UnitTests.Fleet;

/// <summary>Exercises real composition with controlled module ports; no database or HTTP host is used.</summary>
public sealed class FleetReadServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        var ports = new Ports();
        Assert.Throws<ArgumentNullException>(() => new FleetReadService(null!, ports, new Clock()));
        Assert.Throws<ArgumentNullException>(() => new FleetReadService(ports, null!, new Clock()));
        Assert.Throws<ArgumentNullException>(() => new FleetReadService(ports, ports, null!));
    }

    [Fact]
    public async Task EmptyFleet_HasNullMeanAndSkipsTelemetry()
    {
        var ports = new Ports();
        var result = await Read(ports);
        Assert.Empty(result.Rows);
        Assert.Equal(new FleetMetrics(0, 0, 0, 0, 0, null, 0), result.Metrics);
        Assert.Equal(Now, result.AsOfUtc);
        Assert.Equal(0, ports.TelemetryCalls);
    }

    [Fact]
    public async Task MixedFleet_ComposesMetadataAndGlobalCounts_ExcludesStaleAndMissingSamples()
    {
        var devices = Enumerable.Range(0, 6).Select(_ => Device()).ToArray();
        var ports = new Ports { Devices = devices, States = new[]
        {
            State(devices[0], 40, 30),
            State(devices[1], 41, 81), // CPU alone degrades, but still contributes latency.
            State(devices[2], 600, 30), // Latency alone degrades.
            State(devices[3], 1000, 90, ageSeconds: 91),
            State(devices[4], null, null, PingOutcome.Timeout)
        }};
        var result = await Read(ports);
        Assert.Equal(new FleetMetrics(6, 1, 2, 2, 1, 227m, 3), result.Metrics);
        Assert.Equal(new[] { HealthState.Healthy, HealthState.Degraded, HealthState.Degraded,
            HealthState.Offline, HealthState.Offline, HealthState.Unknown }, result.Rows.Select(r => r.Status));
        Assert.Equal(devices.Select(d => d.DeviceId), ports.Requested);
        Assert.Equal(new FleetRow(devices[0].DeviceId, "Switch", "TAG", "LON", "Server",
            HealthState.Healthy, Now, 40, 30), result.Rows[0]);
        Assert.Equal(1000, result.Rows[3].LatencyMs);
        Assert.Equal(90m, result.Rows[3].CpuPercent);
        Assert.Null(result.Rows[4].LatencyMs);
        Assert.Null(result.Rows[5].LastObservedAtUtc);
        Assert.Null(result.Rows[5].CpuPercent);
    }

    [Fact]
    public async Task Mean_PreservesFractionalLatency()
    {
        var a = Device(); var b = Device();
        var result = await Read(new Ports { Devices = new[] { a, b }, States = new[] { State(a, 40, 30), State(b, 41, 30) } });
        Assert.Equal(40.5m, result.Metrics.AverageLatencyMs);
        Assert.Equal(2, result.Metrics.SampleCount);
    }

    [Fact]
    public async Task NoFreshSuccesses_HasNullMean()
    {
        var a = Device(); var b = Device(); var c = Device();
        var result = await Read(new Ports { Devices = new[] { a, b, c },
            States = new[] { State(a, 40, 30, ageSeconds: 91), State(b, null, null, PingOutcome.Timeout) } });
        Assert.Equal(new FleetMetrics(3, 0, 0, 2, 1, null, 0), result.Metrics);
    }

    [Theory]
    [InlineData(90, 250, 80, HealthState.Healthy)]
    [InlineData(91, 250, 80, HealthState.Offline)]
    [InlineData(0, 251, 80, HealthState.Degraded)]
    [InlineData(0, 250, 81, HealthState.Degraded)]
    [InlineData(-1, 40, 30, HealthState.Healthy)]
    public async Task Health_UsesCanonicalBoundaryRules(int age, int latency, int cpu, HealthState expected)
    {
        var device = Device();
        var result = await Read(new Ports { Devices = new[] { device }, States = new[] { State(device, latency, cpu, ageSeconds: age) } });
        Assert.Equal(expected, Assert.Single(result.Rows).Status);
    }

    [Fact]
    public async Task Clock_CapturedOnceBeforeBothReads_DespiteElapsedTime()
    {
        var clock = new Clock();
        var device = Device();
        var ports = new Ports { Devices = new[] { device }, States = new[] { State(device, 40, 30, ageSeconds: 90) } };
        ports.OnCatalog = () => clock.Utc = Now.AddHours(1);
        ports.OnTelemetry = () => clock.Utc = Now.AddHours(2);
        var result = await new FleetReadService(ports, ports, clock).ReadAsync();
        Assert.Equal(1, clock.Reads);
        Assert.Equal(Now, result.AsOfUtc);
        Assert.Equal(HealthState.Healthy, Assert.Single(result.Rows).Status);
    }

    [Fact]
    public async Task ExactCapacity_IsComplete_OverflowFailsBeforeTelemetry()
    {
        var ports = new Ports { Devices = Enumerable.Range(0, 1000).Select(_ => Device()).ToArray() };
        var result = await Read(ports);
        Assert.Equal(1000, result.Metrics.Total);
        Assert.Equal(1000, result.Metrics.Unknown);
        Assert.Equal(1000, ports.Requested.Count);
        ports.Devices = ports.Devices.Append(Device()).ToArray();
        await Assert.ThrowsAsync<DeviceCatalogCapacityExceededException>(() => Read(ports));
        Assert.Equal(1, ports.TelemetryCalls);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("retired")]
    [InlineData("null")]
    public async Task InvalidCatalog_RejectsBeforeTelemetry(string kind)
    {
        var device = Device();
        var ports = new Ports { Devices = kind switch
        {
            "empty" => new[] { device with { DeviceId = Guid.Empty } },
            "duplicate" => new[] { device, device },
            "retired" => new[] { device with { IsRetired = true } },
            _ => new DeviceRegistration[] { null! }
        }};
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(ports));
        Assert.Equal(0, ports.TelemetryCalls);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unrequested")]
    [InlineData("null")]
    public async Task InvalidTelemetryIdentity_RejectsInsteadOfProducingMetrics(string kind)
    {
        var device = Device(); var state = State(device, 40, 30);
        var ports = new Ports { Devices = new[] { device }, States = kind switch
        {
            "duplicate" => new[] { state, state },
            "unrequested" => new[] { state with { DeviceId = Guid.NewGuid() } },
            _ => new DeviceTelemetrySnapshot[] { null! }
        }};
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(ports));
    }

    [Fact]
    public async Task InvalidObservation_PropagatesDomainValidation()
    {
        var device = Device();
        await Assert.ThrowsAsync<ArgumentException>(() => Read(new Ports { Devices = new[] { device },
            States = new[] { State(device, 40, 30, PingOutcome.Timeout) } }));
    }

    [Fact]
    public async Task InvalidClock_RejectsEvenEmptyFleet()
    {
        var ports = new Ports();
        await Assert.ThrowsAsync<ArgumentException>(() => new FleetReadService(ports, ports, new Clock { Utc = default }).ReadAsync());
        Assert.Equal(0, ports.CatalogCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderFailure_PropagatesSameException(bool telemetry)
    {
        var failure = new InvalidOperationException("owned fake failure");
        var ports = new Ports { Devices = new[] { Device() } };
        if (telemetry) ports.OnTelemetry = () => throw failure;
        else ports.OnCatalog = () => throw failure;
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => Read(ports)));
    }

    [Theory]
    [InlineData("before")]
    [InlineData("catalog")]
    [InlineData("telemetry")]
    public async Task Cancellation_StopsAtEachBoundary_EvenWhenPortIgnoresToken(string when)
    {
        using var cancellation = new CancellationTokenSource();
        var ports = new Ports { Devices = new[] { Device() } };
        if (when == "before") cancellation.Cancel();
        if (when == "catalog") ports.OnCatalog = cancellation.Cancel;
        if (when == "telemetry") ports.OnTelemetry = cancellation.Cancel;
        var clock = new Clock();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new FleetReadService(ports, ports, clock).ReadAsync(cancellation.Token));
        Assert.Equal(when == "before" ? 0 : 1, ports.CatalogCalls);
        Assert.Equal(when == "telemetry" ? 1 : 0, ports.TelemetryCalls);
        Assert.Equal(when == "before" ? default : cancellation.Token, ports.LastToken);
        if (when == "before") Assert.Equal(0, clock.Reads);
    }

    [Fact]
    public async Task ResultsAndRequestIds_AreFrozen_AndCatalogIsSnapshottedBeforeSecondRead()
    {
        var original = Device();
        var registrations = new[] { original };
        var ports = new Ports { Devices = registrations };
        ports.OnTelemetry = () => registrations[0] = Device();
        var result = await Read(ports);
        Assert.Equal(original.DeviceId, Assert.Single(result.Rows).DeviceId);
        var rows = Assert.IsAssignableFrom<IList<FleetRow>>(result.Rows);
        Assert.Throws<NotSupportedException>(() => rows[0] = rows[0] with { DisplayName = "changed" });
        var requested = Assert.IsAssignableFrom<IList<Guid>>(ports.Requested);
        Assert.Throws<NotSupportedException>(() => requested[0] = Guid.NewGuid());
    }

    [Fact]
    public async Task SequentialAsyncReads_PreserveOneClockAndCompleteOnlyAfterTelemetry()
    {
        var device = Device();
        var catalog = new TaskCompletionSource<IReadOnlyList<DeviceRegistration>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var telemetry = new TaskCompletionSource<IReadOnlyList<DeviceTelemetrySnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new Clock();
        var ports = new Ports { CatalogResult = catalog.Task, TelemetryResult = telemetry.Task,
            OnTelemetry = () => entered.TrySetResult() };
        var pending = new FleetReadService(ports, ports, clock).ReadAsync();
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, ports.TelemetryCalls);
        clock.Utc = Now.AddHours(1);
        catalog.SetResult(new[] { device });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);
        clock.Utc = Now.AddHours(2);
        telemetry.SetResult(new[] { State(device, 40, 30, ageSeconds: 90) });
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Now, result.AsOfUtc);
        Assert.Equal(1, clock.Reads);
        Assert.Equal(HealthState.Healthy, Assert.Single(result.Rows).Status);
    }

    [Fact]
    public async Task ZeroLatency_IsARealSample_NotAnAbsentMean()
    {
        var device = Device();
        var result = await Read(new Ports { Devices = new[] { device }, States = new[] { State(device, 0, 0) } });
        Assert.Equal(0m, result.Metrics.AverageLatencyMs);
        Assert.Equal(1, result.Metrics.SampleCount);
    }
    /// <summary>Creates consistent active inventory metadata; random IDs are local values only.</summary>
    private static DeviceRegistration Device() => new(Guid.NewGuid(), "Switch", "TAG", "LON", "Server", false);

    /// <summary>Builds explicit observation facts; callers can intentionally construct contradictions to test rejection.</summary>
    private static DeviceTelemetrySnapshot State(DeviceRegistration device, int? latency, decimal? cpu,
        PingOutcome outcome = PingOutcome.Success, int ageSeconds = 0) =>
        new(device.DeviceId, Now.AddSeconds(-ageSeconds), null, outcome, latency, cpu);

    /// <summary>Invokes the real composition with a fresh deterministic clock.</summary>
    private static Task<FleetSnapshot> Read(Ports ports) => new FleetReadService(ports, ports, new Clock()).ReadAsync();

    /// <summary>Counts time reads while allowing port callbacks to simulate elapsed request time without sleeping.</summary>
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Utc = Now;
        internal int Reads;
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() { Reads++; return Utc; }
    }

    /// <summary>Independent query-port fake; callbacks deliberately ignore cancellation to exercise coordinator checks.</summary>
    /// <remarks>No persistence or mutable entities are shared between modules. Requested identities are retained to verify freezing.</remarks>
    private sealed class Ports : IDeviceCatalogQueries, IDeviceHealthQueries
    {
        internal IReadOnlyList<DeviceRegistration> Devices = Array.Empty<DeviceRegistration>();
        internal IReadOnlyList<DeviceTelemetrySnapshot> States = Array.Empty<DeviceTelemetrySnapshot>();
        internal Task<IReadOnlyList<DeviceRegistration>>? CatalogResult;
        internal Task<IReadOnlyList<DeviceTelemetrySnapshot>>? TelemetryResult;
        internal Action? OnCatalog;
        internal Action? OnTelemetry;
        internal int CatalogCalls, TelemetryCalls;
        internal IReadOnlyCollection<Guid> Requested = Array.Empty<Guid>();
        internal CancellationToken LastToken;

        /// <inheritdoc />
        public Task<IReadOnlyList<DeviceRegistration>> GetActiveAsync(CancellationToken cancellationToken = default)
        {
            CatalogCalls++; LastToken = cancellationToken; OnCatalog?.Invoke();
            return CatalogResult ?? Task.FromResult(Devices);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<DeviceTelemetrySnapshot>> GetLatestByDeviceIdsAsync(
            IReadOnlyCollection<Guid> deviceIds, CancellationToken cancellationToken = default)
        {
            TelemetryCalls++; LastToken = cancellationToken; Requested = deviceIds; OnTelemetry?.Invoke();
            return TelemetryResult ?? Task.FromResult(States);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<DeviceRegistration>> GetByIdsAsync(
            IReadOnlyCollection<Guid> deviceIds, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Fleet composition must use active inventory only.");
    }
}
