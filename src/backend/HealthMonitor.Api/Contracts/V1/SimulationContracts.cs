using System.ComponentModel.DataAnnotations;

namespace HealthMonitor.Api.Contracts.V1;

/// <summary>Allows only server-generated synthetic scenarios; no measurements, addresses, or observation timestamps are accepted.</summary>
public sealed class SimulationBatchRequestDto
{
    /// <summary>Caller-owned UUIDv7, preserved on retries of the same logical action.</summary>
    [Required] public Guid? BatchId { get; init; }
    /// <summary>One to twenty-five distinct nonempty registered identifiers; current eligibility is Application-owned after receipt lookup.</summary>
    [Required, MinLength(1), MaxLength(25)] public Guid[]? DeviceIds { get; init; }
    /// <summary>Exact Healthy, HighLatency, HighCpu, or Timeout token.</summary>
    [Required] public string? Scenario { get; init; }
}

/// <summary>Reports an acknowledged commit or saved replay without adding another data envelope.</summary>
/// <param name="BatchId">Original request identity.</param>
/// <param name="AffectedDeviceCount">Committed selected-device count.</param>
/// <param name="CommittedAtUtc">UTC DateTime emits the required Z wire suffix.</param>
/// <param name="Replayed">True when the saved receipt supplied the result.</param>
public sealed record SimulationBatchResultDto(Guid BatchId, int AffectedDeviceCount, DateTime CommittedAtUtc, bool Replayed);
