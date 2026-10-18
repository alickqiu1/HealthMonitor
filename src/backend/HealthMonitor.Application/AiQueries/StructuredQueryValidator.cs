namespace HealthMonitor.Application.AiQueries;

/// <summary>Validates proposal semantics without persistence, provider SDKs, or arbitrary expressions.</summary>
/// <remarks>This does not assess whether natural-language intent was understood correctly. Strict JSON shape
/// validation belongs to the adapter; the read API independently revalidates applied filters.</remarks>
public static class StructuredQueryValidator
{
    /// <summary>Returns a normalized decision, or null when an untrusted proposal is invalid.</summary>
    /// <param name="proposal">Untrusted candidate, including null for an absent proposal.</param>
    /// <param name="vocabulary">Owned canonical seed vocabulary.</param>
    /// <returns>A trusted decision with fixed text, or null without echoing candidate content.</returns>
    /// <exception cref="ArgumentNullException">The owned vocabulary is null.</exception>
    public static QueryTranslationResult? Validate(StructuredQueryProposal? proposal, QueryVocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        if (proposal is null) return null;
        if (proposal.Outcome == "NeedsClarification" && proposal.Query is null
            && proposal.ReasonCode is "ambiguous" or "no_interpretation")
            return new("NeedsClarification", null,
                "Please name Healthy, Degraded, Offline, or Unknown and any desired site or device type.");
        if (proposal.Outcome == "Unsupported" && proposal.Query is null && proposal.ReasonCode == "unsupported")
            return new("Unsupported", null, "Only supported device filter queries can be proposed.");
        if (proposal.Outcome != "Ready" || proposal.ReasonCode != "ready" || proposal.Query is not { } query)
            return null;

        var q = query.Q?.Trim();
        // Search is literal data, even when it resembles SQL. A blacklist would change the read contract.
        if (q is not null && q.Length is < 1 or > 100) return null;
        string? site = null, type = null;
        if (query.Site is not null && !vocabulary.TrySite(query.Site, out site)) return null;
        if (query.Type is not null && !vocabulary.TryType(query.Type, out type)) return null;
        if (query.Status is not (null or "Unknown" or "Healthy" or "Degraded" or "Offline")) return null;
        // These tokens are exact, unlike canonicalizable site/type values. Missing fields get no defaults.
        if (query.Sort is not ("displayName" or "assetTag" or "siteCode" or "status"
            or "lastObservedAtUtc" or "latencyMs" or "cpuPercent")) return null;
        if (query.Direction is not ("asc" or "desc")) return null;
        return new("Ready", new(q, site, type, query.Status, query.Sort, query.Direction),
            "Review the proposed filters before applying them.");
    }
}
