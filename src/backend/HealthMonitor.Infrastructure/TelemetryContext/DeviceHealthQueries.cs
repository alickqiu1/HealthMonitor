using HealthMonitor.Application.IntegrationContracts;
using HealthMonitor.Application.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace HealthMonitor.Infrastructure.Data.TelemetryContext;

/// <summary>Projects requested latest Telemetry states independently of Device persistence and health evaluation.</summary>
public sealed class DeviceHealthQueries : IDeviceHealthQueries
{
    private readonly TelemetryDbContext _context;

    /// <summary>Creates a scoped read adapter without opening a database connection.</summary>
    /// <param name="context">Caller-owned scoped Telemetry context; use sequentially.</param>
    /// <exception cref="ArgumentNullException">The context is null.</exception>
    public DeviceHealthQueries(TelemetryDbContext context) => _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceTelemetrySnapshot>> GetLatestByDeviceIdsAsync(
        IReadOnlyCollection<Guid> deviceIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        cancellationToken.ThrowIfCancellationRequested();
        // Materialize before awaiting: a caller can mutate the original list while connection/query work is suspended.
        var requested = deviceIds.Distinct().ToArray();
        if (requested.Length > IDeviceHealthQueries.MaximumDeviceIdsPerQuery)
            throw new ArgumentException("At most 1,000 distinct telemetry device IDs may be requested.", nameof(deviceIds));
        var ids = requested.Where(id => id != Guid.Empty).ToArray();
        if (ids.Length == 0) return Array.Empty<DeviceTelemetrySnapshot>();

        return await _context.DeviceTelemetryStates.AsNoTracking()
            .Where(state => ids.Contains(state.DeviceId))
            .OrderBy(state => state.DeviceId)
            .Select(state => new DeviceTelemetrySnapshot(state.DeviceId, state.LastObservedAtUtc,
                state.LastSuccessfulAtUtc, state.LatestOutcome, state.LatestLatencyMs, state.LatestCpuPercent))
            .ToListAsync(cancellationToken);
    }
}
