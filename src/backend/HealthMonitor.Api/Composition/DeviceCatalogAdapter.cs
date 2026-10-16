using HealthMonitor.Application.Device;
using HealthMonitor.Application.IntegrationContracts;
using HealthMonitor.Application.Telemetry;

namespace HealthMonitor.Api.Composition;

/// <summary>Bridges Telemetry's catalog port to Device's application query boundary.</summary>
/// <remarks>
/// The API owns cross-module wiring. This adapter preserves the task, result, cancellation,
/// and failures without mapping, caching, persistence access, or eligibility decisions.
/// </remarks>
internal sealed class DeviceCatalogAdapter : IDeviceCatalog
{
    private readonly IDeviceCatalogQueries _queries;

    /// <summary>Creates the bridge using the scoped Device query service.</summary>
    /// <param name="queries">Non-null Device application boundary.</param>
    /// <exception cref="ArgumentNullException">The query service is null.</exception>
    public DeviceCatalogAdapter(IDeviceCatalogQueries queries)
    {
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DeviceRegistration>> GetByIdsAsync(
        IReadOnlyCollection<Guid> deviceIds,
        CancellationToken cancellationToken = default) =>
        _queries.GetByIdsAsync(deviceIds, cancellationToken);
}
