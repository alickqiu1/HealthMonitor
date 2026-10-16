namespace HealthMonitor.Application.Device;

/// <summary>Signals that complete fleet composition exceeds Edition 1.5's supported capacity.</summary>
/// <remarks>
/// Allows a future API boundary to distinguish capacity from dependency failure without
/// exposing persistence types. No partial catalog should be presented as the whole fleet.
/// </remarks>
public sealed class DeviceCatalogCapacityExceededException : InvalidOperationException
{
    /// <summary>Creates the capacity failure for the fixed active-fleet limit.</summary>
    public DeviceCatalogCapacityExceededException()
        : base($"The active device catalog exceeds the supported limit of {IDeviceCatalogQueries.MaximumActiveDevices} devices.")
    {
    }
}
