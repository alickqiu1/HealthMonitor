using HealthMonitor.Application.Device;
using HealthMonitor.Application.IntegrationContracts;
using HealthMonitor.Application.Telemetry;
using HealthMonitor.Domain.Telemetry;

namespace HealthMonitor.Api.Fleet;

/// <summary>Composes bounded active fleet through application ports without cross-module joins.</summary>
/// <remarks>Use sequentially within a scope. No writes, HTTP binding, sorting policy, or pagination occur here.</remarks>
internal sealed class FleetReadService
{
    private readonly IDeviceCatalogQueries catalog;
    private readonly IDeviceHealthQueries telemetry;
    private readonly TimeProvider clock;

    /// <summary>Creates a scoped reader with separately owned module queries.</summary>
    /// <param name="catalog">Complete active inventory boundary.</param>
    /// <param name="telemetry">Selected-identity latest-state boundary.</param>
    /// <param name="clock">Clock read once per admitted request.</param>
    /// <exception cref="ArgumentNullException">A dependency is null.</exception>
    public FleetReadService(IDeviceCatalogQueries catalog, IDeviceHealthQueries telemetry, TimeProvider clock)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Reads active inventory and matching states, then freezes rows and unfiltered metrics.</summary>
    /// <param name="cancellationToken">Cancellation for sequential reads and composition.</param>
    /// <returns>Complete rows and metrics evaluated at one captured UTC instant.</returns>
    /// <exception cref="DeviceCatalogCapacityExceededException">The active fleet exceeds supported capacity.</exception>
    /// <exception cref="InvalidOperationException">A port violates its identity, uniqueness, or active-only contract.</exception>
    /// <exception cref="ArgumentException">The clock or observation violates the Domain health contract.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <remarks>Provider failures propagate. Only missing states mean Unknown. Reads may see commits after asOfUtc;
    /// the captured instant does not promise a historical or cross-module serializable snapshot.</remarks>
    internal async Task<FleetSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var asOfUtc = clock.GetUtcNow();
        // Empty fleets must validate the clock too, even though no row invokes the policy.
        TelemetryHealthPolicy.Classify(asOfUtc, null, null, null, null);
        var registrations = (await catalog.GetActiveAsync(cancellationToken)).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (registrations.Length > IDeviceCatalogQueries.MaximumActiveDevices)
            throw new DeviceCatalogCapacityExceededException();
        var ids = new HashSet<Guid>();
        foreach (var device in registrations)
        {
            if (device is null || device.DeviceId == Guid.Empty || device.IsRetired || !ids.Add(device.DeviceId))
                throw new InvalidOperationException("The active catalog returned an invalid, retired, or duplicate identity.");
        }

        var states = new Dictionary<Guid, DeviceTelemetrySnapshot>();
        if (ids.Count != 0)
        {
            // Read-only identities prevent adapters from mutating the selected set during an asynchronous read.
            var snapshots = await telemetry.GetLatestByDeviceIdsAsync(Array.AsReadOnly(ids.ToArray()), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var state in snapshots)
            {
                if (state is null || !ids.Contains(state.DeviceId) || !states.TryAdd(state.DeviceId, state))
                    throw new InvalidOperationException("Telemetry returned an unrequested or duplicate identity.");
            }
        }

        var rows = new FleetRow[registrations.Length];
        int healthy = 0, degraded = 0, offline = 0, unknown = 0, sampleCount = 0;
        decimal latencySum = 0;
        for (int i = 0; i < registrations.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var device = registrations[i];
            states.TryGetValue(device.DeviceId, out var state);
            var status = TelemetryHealthPolicy.Classify(asOfUtc, state?.LastObservedAtUtc,
                state?.LatestOutcome, state?.LatestLatencyMs, state?.LatestCpuPercent);
            switch (status)
            {
                case HealthState.Healthy: healthy++; break;
                case HealthState.Degraded: degraded++; break;
                case HealthState.Offline: offline++; break;
                case HealthState.Unknown: unknown++; break;
            }
            // These are exactly the fresh-success cases; stale numeric values must not affect the mean.
            if (status is HealthState.Healthy or HealthState.Degraded)
            {
                latencySum += state!.LatestLatencyMs!.Value;
                sampleCount++;
            }
            rows[i] = new FleetRow(device.DeviceId, device.DisplayName, device.AssetTag, device.SiteCode,
                device.DeviceType, status, state?.LastObservedAtUtc, state?.LatestLatencyMs, state?.LatestCpuPercent);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new FleetSnapshot(asOfUtc, Array.AsReadOnly(rows), new FleetMetrics(rows.Length,
            healthy, degraded, offline, unknown, sampleCount == 0 ? null : latencySum / sampleCount, sampleCount));
    }
}
