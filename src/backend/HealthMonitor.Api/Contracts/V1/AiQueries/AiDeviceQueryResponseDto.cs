namespace HealthMonitor.Api.Contracts.V1.AiQueries;

/// <summary>Explicit nullable filters proposed for later independent GET validation.</summary>
/// <param name="Q">Literal search or null.</param>
/// <param name="Site">Canonical supported site or null.</param>
/// <param name="Type">Canonical category or null.</param>
/// <param name="Status">Canonical health token or null.</param>
/// <param name="Sort">One of seven allowlisted sort keys.</param>
/// <param name="Direction">asc or desc.</param>
public sealed record DeviceGridQueryDto(string? Q, string? Site, string? Type, string? Status,
    string Sort, string Direction)
{
    /// <summary>Keeps proposed literal search data out of generic object diagnostics.</summary>
    public override string ToString() => nameof(DeviceGridQueryDto);
}

/// <summary>Server-owned V1 translation decision; it neither executes nor applies the proposed filters.</summary>
/// <param name="Outcome">Validated Ready, NeedsClarification, or Unsupported.</param>
/// <param name="Query">Normalized query for Ready; otherwise null.</param>
/// <param name="Message">Fixed plain-text message, never provider narrative.</param>
public sealed record AiDeviceQueryResponseDto(string Outcome, DeviceGridQueryDto? Query, string Message)
{
    /// <summary>Server-owned contract version, independent of provider output.</summary>
    public string ContractVersion => "1";

    /// <summary>Keeps proposed filters out of MVC result diagnostics without changing wire serialization.</summary>
    public override string ToString() => nameof(AiDeviceQueryResponseDto);
}
