namespace HealthMonitor.Application.AiQueries;

/// <summary>Defensive snapshot of the composition root's nonsecret seed filter vocabulary.</summary>
/// <remarks>No database lookup or inventory invariant is introduced. Empty vocabularies permit only null
/// filters. Conflicting canonical spellings are rejected instead of depending on enumeration order.</remarks>
public sealed class QueryVocabulary
{
    private readonly Dictionary<string, string> sites;
    private readonly Dictionary<string, string> types;

    /// <summary>Copies the canonical site and category values supplied by the owning composition root.</summary>
    /// <param name="sites">Canonical sites; exact duplicates are harmless.</param>
    /// <param name="types">Canonical categories, retaining their supplied spelling.</param>
    /// <exception cref="ArgumentNullException">A sequence is null.</exception>
    /// <exception cref="ArgumentException">A token is blank/untrimmed or has a conflicting canonical spelling.</exception>
    public QueryVocabulary(IEnumerable<string> sites, IEnumerable<string> types)
    {
        this.sites = Snapshot(sites, nameof(sites));
        this.types = Snapshot(types, nameof(types));
        Sites = Array.AsReadOnly(this.sites.Values.ToArray());
        Types = Array.AsReadOnly(this.types.Values.ToArray());
    }

    /// <summary>Read-only canonical sites, independent of the caller's original collection.</summary>
    public IReadOnlyList<string> Sites { get; }
    /// <summary>Read-only canonical categories, independent of the caller's original collection.</summary>
    public IReadOnlyList<string> Types { get; }

    /// <summary>Resolves a trimmed site to its owned canonical spelling.</summary>
    internal bool TrySite(string token, out string? canonical) => sites.TryGetValue(token.Trim(), out canonical);
    /// <summary>Resolves a trimmed category to its owned canonical spelling.</summary>
    internal bool TryType(string token, out string? canonical) => types.TryGetValue(token.Trim(), out canonical);

    /// <summary>Copies seed values without echoing invalid configuration contents in errors.</summary>
    private static Dictionary<string, string> Snapshot(IEnumerable<string> values, string parameter)
    {
        ArgumentNullException.ThrowIfNull(values, parameter);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || value != value.Trim()
                || (result.TryGetValue(value, out var existing) && existing != value))
                throw new ArgumentException("Filter vocabulary contains an invalid or conflicting canonical token.", parameter);
            result.TryAdd(value, value);
        }
        return result;
    }
}
