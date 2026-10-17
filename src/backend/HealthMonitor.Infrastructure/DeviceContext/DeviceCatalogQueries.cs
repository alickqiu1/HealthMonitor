using HealthMonitor.Application.Device;
using HealthMonitor.Application.IntegrationContracts;
using Microsoft.EntityFrameworkCore;

namespace HealthMonitor.Infrastructure.Data.DeviceContext;

/// <summary>Reads Device-owned inventory through the existing scoped Device context.</summary>
/// <remarks>
/// Uses no-tracking SQL projections of only registration fields. It owns no connection,
/// transaction, or pool and never saves changes. The caller owns the context's lifetime.
/// No-tracking reads express the read-only contract and keep inventory composition independent
/// of tracked entity state. Scalar projections do not materialize entities in the first place.
/// Snapshot consistency follows the caller's transaction; separate module reads do not establish a shared atomic snapshot.
/// </remarks>
public sealed class DeviceCatalogQueries : IDeviceCatalogQueries
{
    private readonly DeviceDbContext _context;

    /// <summary>Uses the context already configured by the composition root.</summary>
    /// <param name="context">Non-null, scoped Device context; not owned by this service.</param>
    /// <exception cref="ArgumentNullException">The context is null.</exception>
    public DeviceCatalogQueries(DeviceDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceRegistration>> GetByIdsAsync(
        IReadOnlyCollection<Guid> deviceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        cancellationToken.ThrowIfCancellationRequested();

        // Snapshot and deduplicate the input; Npgsql parameterizes this array rather
        // than interpolating identifiers into SQL. Ingestion's 25-ID rule lives elsewhere.
        Guid[] ids = deviceIds.Distinct().ToArray();
        if (ids.Length == 0)
            return Array.Empty<DeviceRegistration>();

        // Explicit lookup intentionally retains retired devices for historical reads
        // and for the ingestion workflow to distinguish retirement from an unknown ID.
        return await Project(_context.Devices.AsNoTracking()
                .Where(device => ids.Contains(device.Id))
                .OrderBy(device => device.Id))
            .ToArrayAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceRegistration>> GetActiveAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Read one overflow sentinel in the same statement. A separate count and list
        // could disagree under concurrent changes; Take(1000) alone would hide overflow.
        DeviceRegistration[] registrations = await Project(_context.Devices.AsNoTracking()
                .Where(device => device.RetiredAtUtc == null)
                .OrderBy(device => device.Id)
                .Take(IDeviceCatalogQueries.MaximumActiveDevices + 1))
            .ToArrayAsync(cancellationToken);

        if (registrations.Length > IDeviceCatalogQueries.MaximumActiveDevices)
            throw new DeviceCatalogCapacityExceededException();

        return registrations;
    }

    /// <summary>Projects the six cross-module fields without materializing domain entities.</summary>
    /// <param name="devices">Device-owned query with filters and ordering already applied.</param>
    /// <returns>A composable internal projection, executed only by this persistence service.</returns>
    private static IQueryable<DeviceRegistration> Project(
        IQueryable<HealthMonitor.Domain.Device.MonitoredDevice> devices) =>
        devices.Select(device => new DeviceRegistration(
            device.Id, device.DisplayName, device.AssetTag, device.SiteCode,
            device.DeviceType, device.RetiredAtUtc != null));
}
