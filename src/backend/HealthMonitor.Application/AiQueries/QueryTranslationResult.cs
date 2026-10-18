namespace HealthMonitor.Application.AiQueries;

/// <summary>Validated immutable translation decision with a server-owned message.</summary>
/// <remarks>There is no public constructor or mutable state: callers cannot manufacture or change a trusted
/// Ready result. Candidate query records are immutable; a record copy does not change the contained instance.</remarks>
public sealed class QueryTranslationResult
{
    /// <summary>Creates a decision only after Application validation.</summary>
    internal QueryTranslationResult(string outcome, DeviceGridQuery? query, string message)
    {
        Outcome = outcome;
        Query = query;
        Message = message;
    }

    /// <summary>Validated Ready, NeedsClarification, or Unsupported token.</summary>
    public string Outcome { get; }
    /// <summary>Normalized filters for Ready; otherwise null.</summary>
    public DeviceGridQuery? Query { get; }
    /// <summary>Fixed plain text; never provider-supplied narrative.</summary>
    public string Message { get; }
}
