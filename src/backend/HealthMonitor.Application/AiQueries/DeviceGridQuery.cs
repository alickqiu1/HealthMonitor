namespace HealthMonitor.Application.AiQueries;

/// <summary>Immutable, untrusted candidate filters proposed by a query generator.</summary>
/// <remarks>Construction does not validate these values. Only the validator creates a trusted translation result.
/// Paging is deliberately absent: applying a proposal resets the page and preserves the client's valid page size.</remarks>
/// <param name="Q">Optional literal display-name or asset-tag search.</param>
/// <param name="Site">Optional site from the supplied vocabulary.</param>
/// <param name="Type">Optional device category from the supplied vocabulary.</param>
/// <param name="Status">Optional exact health token.</param>
/// <param name="Sort">Required exact sort key; no implicit default.</param>
/// <param name="Direction">Required exact direction token.</param>
public sealed record DeviceGridQuery(string? Q, string? Site, string? Type, string? Status,
    string? Sort, string? Direction);
