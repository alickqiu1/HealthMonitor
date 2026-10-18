namespace HealthMonitor.Api.Contracts.V1.AiQueries;

/// <summary>Untrusted user text submitted explicitly for filter translation.</summary>
/// <remarks>The route's strict body filter requires exactly one string field; Application owns trimmed prompt bounds.</remarks>
/// <param name="Prompt">User text, never an instruction to query storage or execute a tool.</param>
public sealed record AiDeviceQueryRequestDto(string? Prompt)
{
    /// <summary>Keeps MVC argument diagnostics from formatting the submitted prompt.</summary>
    public override string ToString() => nameof(AiDeviceQueryRequestDto);
}
