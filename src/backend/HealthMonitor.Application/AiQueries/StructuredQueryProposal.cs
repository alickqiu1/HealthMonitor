namespace HealthMonitor.Application.AiQueries;

/// <summary>Untrusted provider-neutral proposal, before semantic validation.</summary>
/// <remarks>The adapter must separately reject missing, duplicate, extra, or incorrectly typed JSON fields.
/// A typed proposal cannot prove that structural parsing was strict.</remarks>
/// <param name="Outcome">Exact translation outcome token.</param>
/// <param name="Query">Candidate filters, permitted only for Ready.</param>
/// <param name="ReasonCode">Exact allowlisted reason paired with the outcome.</param>
public sealed record StructuredQueryProposal(string? Outcome, DeviceGridQuery? Query, string? ReasonCode);
