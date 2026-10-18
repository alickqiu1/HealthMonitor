namespace HealthMonitor.Application.IntegrationContracts;

/// <summary>Immutable Device-owned registration and lifecycle metadata for a single-device read.</summary>
/// <param name="Registration">Inventory projection; retirement flag agrees with the retirement timestamp.</param>
/// <param name="CreatedAtUtc">Non-default zero-offset UTC registration time.</param>
/// <param name="RetiredAtUtc">Zero-offset retirement time at or after creation, or null while active.</param>
/// <remarks>Separate from DeviceRegistration so ingestion and bounded fleet reads retain their minimal contract.</remarks>
public sealed record DeviceCatalogDetail(DeviceRegistration Registration, DateTimeOffset CreatedAtUtc, DateTimeOffset? RetiredAtUtc);
