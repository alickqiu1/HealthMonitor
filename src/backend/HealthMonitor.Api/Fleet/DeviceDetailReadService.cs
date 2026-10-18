using HealthMonitor.Application.Device;
using HealthMonitor.Application.Telemetry;
using HealthMonitor.Domain.Telemetry;

namespace HealthMonitor.Api.Fleet;

/// <summary>Composes one known device through separate module ports, retaining retired inventory for historical reads.</summary>
/// <remarks>Use sequentially within a scope. Retirement does not determine computed health or suppress historical facts.</remarks>
internal sealed class DeviceDetailReadService
{
    private readonly IDeviceCatalogQueries catalog;
    private readonly IDeviceHealthQueries telemetry;
    private readonly TimeProvider clock;

    /// <summary>Creates a scoped reader without opening storage.</summary>
    /// <param name="catalog">Explicit single-device inventory boundary.</param>
    /// <param name="telemetry">Latest-state boundary.</param>
    /// <param name="clock">Clock read once before either query.</param>
    /// <exception cref="ArgumentNullException">A dependency is null.</exception>
    public DeviceDetailReadService(IDeviceCatalogQueries catalog, IDeviceHealthQueries telemetry, TimeProvider clock)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Returns known active or retired detail; unknown or empty identities return null.</summary>
    /// <param name="deviceId">Requested inventory identity.</param>
    /// <param name="cancellationToken">Cancellation checked before empty/unknown results and after each read.</param>
    /// <returns>Immutable detail, or null without a telemetry query when the inventory is absent.</returns>
    /// <exception cref="InvalidOperationException">A port returns mismatched, duplicate, or inconsistent facts.</exception>
    /// <exception cref="ArgumentException">Clock or observation violates the Domain health contract.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <remarks>Provider failures propagate. Missing telemetry means Unknown, never an invented measurement.
    /// Separate module reads can observe later commits; asOfUtc is an evaluation instant, not historical isolation.</remarks>
    internal async Task<DeviceDetailSnapshot?> ReadAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (deviceId == Guid.Empty) return null;
        var asOfUtc = clock.GetUtcNow();
        TelemetryHealthPolicy.Classify(asOfUtc, null, null, null, null);
        var detail = await catalog.GetDetailAsync(deviceId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (detail is null) return null;
        var device = detail.Registration;
        // Projections can be independently constructed: check identity and lifecycle coherence before exposing metadata.
        if (device is null || device.DeviceId != deviceId || !ValidUtc(detail.CreatedAtUtc) ||
            device.IsRetired != detail.RetiredAtUtc.HasValue ||
            (detail.RetiredAtUtc is { } retired && (!ValidUtc(retired) || retired < detail.CreatedAtUtc)))
            throw new InvalidOperationException("Device detail returned inconsistent identity or lifecycle metadata.");

        var states = await telemetry.GetLatestByDeviceIdsAsync(Array.AsReadOnly(new[] { deviceId }), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (states.Count > 1 || (states.Count == 1 && (states[0] is null || states[0].DeviceId != deviceId)))
            throw new InvalidOperationException("Telemetry detail returned duplicate or unrequested identities.");
        var state = states.SingleOrDefault();
        var status = TelemetryHealthPolicy.Classify(asOfUtc, state?.LastObservedAtUtc,
            state?.LatestOutcome, state?.LatestLatencyMs, state?.LatestCpuPercent);
        // Last-success is displayed in detail; preserve timeout history but reject impossible persisted relationships.
        if (state is not null &&
            ((state.LastSuccessfulAtUtc is { } success && (!ValidUtc(success) || success > state.LastObservedAtUtc)) ||
             (state.LatestOutcome == PingOutcome.Success && state.LastSuccessfulAtUtc != state.LastObservedAtUtc)))
            throw new InvalidOperationException("Telemetry detail returned inconsistent last-success metadata.");
        return new DeviceDetailSnapshot(asOfUtc, new FleetRow(device.DeviceId, device.DisplayName, device.AssetTag,
            device.SiteCode, device.DeviceType, status, state?.LastObservedAtUtc, state?.LatestLatencyMs,
            state?.LatestCpuPercent), detail.CreatedAtUtc, detail.RetiredAtUtc, state?.LastSuccessfulAtUtc);
    }

    /// <summary>Matches the existing Device timestamp invariant without constructing an entity during a read.</summary>
    /// <param name="value">Lifecycle or last-success timestamp.</param>
    /// <returns>True for non-default zero-offset UTC.</returns>
    private static bool ValidUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
}
