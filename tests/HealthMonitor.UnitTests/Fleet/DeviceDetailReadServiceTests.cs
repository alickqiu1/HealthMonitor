using HealthMonitor.Api.Fleet;
using HealthMonitor.Application.Device;
using HealthMonitor.Application.IntegrationContracts;
using HealthMonitor.Application.Telemetry;
using HealthMonitor.Domain.Telemetry;

namespace HealthMonitor.UnitTests.Fleet;

/// <summary>Verifies explicit active/retired detail composition with independent fake ports and no persistence.</summary>
public sealed class DeviceDetailReadServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        var ports = new Ports();
        Assert.Throws<ArgumentNullException>(() => new DeviceDetailReadService(null!, ports, new Clock()));
        Assert.Throws<ArgumentNullException>(() => new DeviceDetailReadService(ports, null!, new Clock()));
        Assert.Throws<ArgumentNullException>(() => new DeviceDetailReadService(ports, ports, null!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownOrEmpty_ReturnsNull_WithoutTelemetry(bool empty)
    {
        var ports = new Ports { Detail = null };
        var result = await Read(ports, empty ? Guid.Empty : ports.Id);
        Assert.Null(result);
        Assert.Equal(empty ? 0 : 1, ports.DetailCalls);
        Assert.Equal(0, ports.TelemetryCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownActiveOrRetired_NoState_IsUnknownWithNullMeasurements(bool retired)
    {
        var ports = new Ports();
        if (retired) ports.Detail = ports.Detail! with
        {
            Registration = ports.Detail!.Registration with { IsRetired = true },
            RetiredAtUtc = Now.AddHours(-1)
        };
        var result = Assert.IsType<DeviceDetailSnapshot>(await Read(ports, ports.Id));
        Assert.Equal(new FleetRow(ports.Id, "Database", "DETAIL", "DEN", "Database", HealthState.Unknown,
            null, null, null), result.Row);
        Assert.Equal(Now.AddDays(-1), result.CreatedAtUtc);
        Assert.Equal(retired ? Now.AddHours(-1) : null, result.RetiredAtUtc);
        Assert.Null(result.LastSuccessfulAtUtc);
        Assert.Equal(ports.Id, Assert.Single(ports.Requested));
    }

    [Theory]
    [InlineData(false, PingOutcome.Success, 0, HealthState.Healthy)]
    [InlineData(true, PingOutcome.Success, 0, HealthState.Healthy)]
    [InlineData(true, PingOutcome.Success, 91, HealthState.Offline)]
    [InlineData(true, PingOutcome.Timeout, 0, HealthState.Offline)]
    public async Task Retirement_DoesNotChangeHealthOrDiscardLastSuccess(bool retired, PingOutcome outcome, int age, HealthState expected)
    {
        var ports = new Ports();
        if (retired) ports.Detail = ports.Detail! with
        {
            Registration = ports.Detail!.Registration with { IsRetired = true },
            RetiredAtUtc = Now.AddHours(-1)
        };
        var observed = Now.AddSeconds(-age);
        var success = outcome == PingOutcome.Success ? observed : observed.AddMinutes(-1);
        ports.States = new[] { new DeviceTelemetrySnapshot(ports.Id, observed, success, outcome,
            outcome == PingOutcome.Success ? 40 : null, outcome == PingOutcome.Success ? 30 : null) };
        var result = Assert.IsType<DeviceDetailSnapshot>(await Read(ports, ports.Id));
        Assert.Equal(expected, result.Row.Status);
        Assert.Equal(success, result.LastSuccessfulAtUtc);
        Assert.Equal(observed, result.Row.LastObservedAtUtc);
        Assert.Equal(outcome == PingOutcome.Success ? 40 : null, result.Row.LatencyMs);
    }

    [Fact]
    public async Task FirstTimeout_PreservesNullLastSuccess()
    {
        var ports = new Ports();
        ports.States = new[] { new DeviceTelemetrySnapshot(ports.Id, Now, null, PingOutcome.Timeout, null, null) };
        var result = Assert.IsType<DeviceDetailSnapshot>(await Read(ports, ports.Id));
        Assert.Equal(HealthState.Offline, result.Row.Status);
        Assert.Null(result.LastSuccessfulAtUtc);
    }

    [Theory]
    [InlineData("wrong-id")]
    [InlineData("null-registration")]
    [InlineData("creation-default")]
    [InlineData("creation-offset")]
    [InlineData("retired-flag-only")]
    [InlineData("retired-time-only")]
    [InlineData("retired-default")]
    [InlineData("retired-offset")]
    [InlineData("retired-before-creation")]
    public async Task InvalidCatalogDetail_RejectsBeforeTelemetry(string kind)
    {
        var ports = new Ports();
        var detail = ports.Detail!;
        ports.Detail = kind switch
        {
            "wrong-id" => detail with { Registration = detail.Registration with { DeviceId = Guid.NewGuid() } },
            "null-registration" => detail with { Registration = null! },
            "creation-default" => detail with { CreatedAtUtc = default },
            "creation-offset" => detail with { CreatedAtUtc = Now.ToOffset(TimeSpan.FromHours(1)) },
            "retired-flag-only" => detail with { Registration = detail.Registration with { IsRetired = true } },
            "retired-time-only" => detail with { RetiredAtUtc = Now },
            "retired-default" => detail with { Registration = detail.Registration with { IsRetired = true }, RetiredAtUtc = default(DateTimeOffset) },
            "retired-offset" => detail with { Registration = detail.Registration with { IsRetired = true }, RetiredAtUtc = Now.ToOffset(TimeSpan.FromHours(1)) },
            _ => detail with { Registration = detail.Registration with { IsRetired = true }, RetiredAtUtc = Now.AddDays(-2) }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(ports, ports.Id));
        Assert.Equal(0, ports.TelemetryCalls);
    }

    [Fact]
    public async Task Retirement_AtCreation_IsValid()
    {
        var ports = new Ports();
        ports.Detail = ports.Detail! with
        {
            Registration = ports.Detail!.Registration with { IsRetired = true },
            RetiredAtUtc = ports.Detail!.CreatedAtUtc
        };
        Assert.NotNull(await Read(ports, ports.Id));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("wrong-id")]
    [InlineData("null")]
    public async Task InvalidTelemetryIdentities_Reject(string kind)
    {
        var ports = new Ports();
        var state = new DeviceTelemetrySnapshot(ports.Id, Now, Now, PingOutcome.Success, 40, 30);
        ports.States = kind switch
        {
            "duplicate" => new[] { state, state },
            "wrong-id" => new[] { state with { DeviceId = Guid.NewGuid() } },
            _ => new DeviceTelemetrySnapshot[] { null! }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(ports, ports.Id));
    }

    [Theory]
    [InlineData("success-null")]
    [InlineData("success-older")]
    [InlineData("future")]
    [InlineData("default")]
    [InlineData("offset")]
    public async Task InconsistentLastSuccess_RejectsInsteadOfExposingInvalidMetadata(string kind)
    {
        var ports = new Ports();
        DateTimeOffset? success = kind switch
        {
            "success-null" => null,
            "success-older" => Now.AddSeconds(-1),
            "future" => Now.AddSeconds(1),
            "default" => default(DateTimeOffset),
            _ => Now.ToOffset(TimeSpan.FromHours(1))
        };
        ports.States = new[] { new DeviceTelemetrySnapshot(ports.Id, Now, success,
            kind.StartsWith("success") ? PingOutcome.Success : PingOutcome.Timeout,
            kind.StartsWith("success") ? 40 : null, kind.StartsWith("success") ? 30 : null) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(ports, ports.Id));
    }

    [Theory]
    [InlineData("before")]
    [InlineData("empty")]
    [InlineData("unknown")]
    [InlineData("catalog")]
    [InlineData("telemetry")]
    public async Task Cancellation_PrecedesAbsentResults_AndIsCheckedAfterIgnoredPortTokens(string when)
    {
        using var cancellation = new CancellationTokenSource();
        var ports = new Ports();
        var id = ports.Id;
        if (when is "before" or "empty") cancellation.Cancel();
        if (when == "empty") id = Guid.Empty;
        if (when is "unknown" or "catalog") ports.OnDetail = cancellation.Cancel;
        if (when == "unknown") ports.Detail = null;
        if (when == "telemetry") ports.OnTelemetry = cancellation.Cancel;
        var failure = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new DeviceDetailReadService(ports, ports, new Clock()).ReadAsync(id, cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(when is "before" or "empty" ? 0 : 1, ports.DetailCalls);
        Assert.Equal(when == "telemetry" ? 1 : 0, ports.TelemetryCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PortFailure_PropagatesSameException(bool telemetry)
    {
        var failure = new InvalidOperationException("owned fake failure");
        var ports = new Ports();
        if (telemetry) ports.OnTelemetry = () => throw failure;
        else ports.OnDetail = () => throw failure;
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => Read(ports, ports.Id)));
    }

    [Fact]
    public async Task AsyncReads_AreSequential_AndCaptureClockOnlyOnce()
    {
        var ports = new Ports();
        var clock = new Clock();
        var detail = new TaskCompletionSource<DeviceCatalogDetail?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = new TaskCompletionSource<IReadOnlyList<DeviceTelemetrySnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ports.DetailResult = detail.Task;
        ports.TelemetryResult = states.Task;
        ports.OnTelemetry = () => entered.TrySetResult();
        var pending = new DeviceDetailReadService(ports, ports, clock).ReadAsync(ports.Id);
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, ports.TelemetryCalls);
        clock.Utc = Now.AddHours(1);
        detail.SetResult(ports.Detail);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);
        var observed = Now.AddSeconds(-90);
        states.SetResult(new[] { new DeviceTelemetrySnapshot(ports.Id, observed, observed, PingOutcome.Success, 250, 80) });
        var result = Assert.IsType<DeviceDetailSnapshot>(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(Now, result.AsOfUtc);
        Assert.Equal(HealthState.Healthy, result.Row.Status);
        Assert.Equal(1, clock.Reads);
    }

    /// <summary>Uses the actual reader and deterministic clock without a host.</summary>
    private static Task<DeviceDetailSnapshot?> Read(Ports ports, Guid id) =>
        new DeviceDetailReadService(ports, ports, new Clock()).ReadAsync(id);

    /// <summary>Allows controlled time advances while counting evaluation-time reads.</summary>
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Utc = Now;
        internal int Reads;
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() { Reads++; return Utc; }
    }

    /// <summary>Supplies only explicit detail and matching state operations; rejects accidental fleet or ingestion lookups.</summary>
    /// <remarks>Optional tasks suspend reads at deterministic await boundaries; callbacks ignore tokens to verify caller safeguards.</remarks>
    private sealed class Ports : IDeviceCatalogQueries, IDeviceHealthQueries
    {
        internal readonly Guid Id = Guid.NewGuid();
        internal DeviceCatalogDetail? Detail;
        internal IReadOnlyList<DeviceTelemetrySnapshot> States = Array.Empty<DeviceTelemetrySnapshot>();
        internal IReadOnlyCollection<Guid> Requested = Array.Empty<Guid>();
        internal int DetailCalls, TelemetryCalls;
        internal Action? OnDetail, OnTelemetry;
        internal Task<DeviceCatalogDetail?>? DetailResult;
        internal Task<IReadOnlyList<DeviceTelemetrySnapshot>>? TelemetryResult;

        /// <summary>Starts with valid active inventory; each test changes only its intended condition.</summary>
        internal Ports() => Detail = new(new DeviceRegistration(Id, "Database", "DETAIL", "DEN", "Database", false), Now.AddDays(-1), null);
        /// <inheritdoc />
        public Task<DeviceCatalogDetail?> GetDetailAsync(Guid deviceId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Id, deviceId);
            DetailCalls++; OnDetail?.Invoke();
            return DetailResult ?? Task.FromResult(Detail);
        }
        /// <inheritdoc />
        public Task<IReadOnlyList<DeviceTelemetrySnapshot>> GetLatestByDeviceIdsAsync(
            IReadOnlyCollection<Guid> deviceIds, CancellationToken cancellationToken = default)
        {
            Requested = deviceIds; TelemetryCalls++; OnTelemetry?.Invoke();
            return TelemetryResult ?? Task.FromResult(States);
        }
        /// <inheritdoc />
        public Task<IReadOnlyList<DeviceRegistration>> GetActiveAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Detail must not request the active fleet.");
        /// <inheritdoc />
        public Task<IReadOnlyList<DeviceRegistration>> GetByIdsAsync(
            IReadOnlyCollection<Guid> deviceIds, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Detail must use its lifecycle projection.");
    }
}
