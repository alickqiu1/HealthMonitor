namespace HealthMonitor.Api.Fleet;

/// <summary>Immutable single-device composition, including retired registrations, before HTTP contract mapping.</summary>
/// <param name="AsOfUtc">Single evaluation instant; not a cross-module historical snapshot.</param>
/// <param name="Row">Inventory and normally classified health, regardless of retirement.</param>
/// <param name="CreatedAtUtc">Inventory creation time.</param>
/// <param name="RetiredAtUtc">Retirement time or null for active inventory.</param>
/// <param name="LastSuccessfulAtUtc">Persisted latest success time; may survive a later timeout.</param>
internal sealed record DeviceDetailSnapshot(DateTimeOffset AsOfUtc, FleetRow Row, DateTimeOffset CreatedAtUtc,
    DateTimeOffset? RetiredAtUtc, DateTimeOffset? LastSuccessfulAtUtc);
