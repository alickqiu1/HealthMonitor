using HealthMonitor.Infrastructure.AiQueries;

namespace HealthMonitor.Api.AiQueries;

/// <summary>Captures validated installation settings without activating AI or touching operational storage.</summary>
/// <remarks>This is deliberately not a record: generated diagnostic output must not print the retained key.
/// Never serialize or log this object. Disabled binding reads only enablement and parent key metadata;
/// enabled binding is reserved for controlled tests until safe production composition is completed.</remarks>
internal sealed class AiQueryOptions
{
    /// <summary>Gets a disabled snapshot without rereading reloadable configuration or retaining any active settings.</summary>
    internal static AiQueryOptions Disabled { get; } = new(false);

    /// <summary>Gets whether the configuration requests AI activation, not whether activation has been completed.</summary>
    internal bool Enabled { get; }
    /// <summary>Gets the selected immutable provider profile, or null while disabled.</summary>
    internal StructuredQueryProviderProfile? Profile { get; }
    /// <summary>Gets the borrowed backend credential, or null while disabled; never include it in diagnostics.</summary>
    internal string? ApiKey { get; }
    /// <summary>Gets the canonical absolute counter filename, or null while disabled.</summary>
    internal string? BudgetCounterPath { get; }

    private AiQueryOptions(bool enabled, StructuredQueryProviderProfile? profile = null,
        string? apiKey = null, string? budgetCounterPath = null)
    { Enabled = enabled; Profile = profile; ApiKey = apiKey; BudgetCounterPath = budgetCounterPath; }

    /// <summary>Reads the existing Boolean leaf contract without reading other AI setting values.</summary>
    /// <param name="configuration">Root configuration; parent key metadata distinguishes explicit null from absence.</param>
    /// <returns>False when enablement is absent; otherwise the strictly parsed Boolean.</returns>
    /// <exception cref="ArgumentNullException">Configuration is null.</exception>
    /// <exception cref="InvalidOperationException">Enablement has an invalid shape/value; only the owned key is reported.</exception>
    internal static bool ReadEnabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var root = configuration.GetSection("AiQueries");
        var flag = root.GetSection("Enabled");
        bool supplied = root.GetChildren().Any(child => child.Key.Equals("Enabled", StringComparison.OrdinalIgnoreCase));
        string? raw = flag.Value;
        bool enabled = false;
        if (root.Value is not null || flag.GetChildren().Any()
            || (supplied && (raw is null || raw != raw.Trim() || !bool.TryParse(raw, out enabled))))
            throw Invalid("Enabled");
        return enabled;
    }

    /// <summary>Builds a snapshot using targeted known leaves; disabled configuration requires no secret or host path.</summary>
    /// <param name="configuration">Root settings, with a backend-owned key only when enabled.</param>
    /// <param name="environment">Actual host paths, required only for enabled lexical counter-path validation.</param>
    /// <returns>Immutable settings; no provider, counter, directory or file is constructed.</returns>
    /// <exception cref="ArgumentNullException">Configuration is null.</exception>
    /// <exception cref="InvalidOperationException">An enabled setting or required host path is invalid.</exception>
    /// <remarks>Path normalization rejects lexical web-root containment. It cannot prove filesystem ownership,
    /// ACLs, symlink targets or provisioning safety; those must be checked separately before activation.</remarks>
    internal static AiQueryOptions FromConfiguration(IConfiguration configuration, IWebHostEnvironment? environment = null)
    {
        if (!ReadEnabled(configuration)) return Disabled;
        var root = configuration.GetSection("AiQueries");
        var limits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TimeoutSeconds"] = "10", ["MaxPromptCharacters"] = "500", ["MaxRequestBodyBytes"] = "8192",
            ["MaxResponseBytes"] = "8192", ["MaxOutputTokens"] = "400", ["RequestsPerClientPerMinute"] = "5",
            ["RequestsGlobalPerMinute"] = "10", ["MaxConcurrentRequests"] = "2", ["DailyRequestLimit"] = "100"
        };
        var supplied = root.GetChildren().Select(section => section.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] names = ["Enabled", "Provider", "ApiKey", "BudgetCounterPath"];
        // Active options reject old Model/BaseUrl knobs instead of silently ignoring an operator's intended target.
        if (supplied.Any(key => !names.Contains(key, StringComparer.OrdinalIgnoreCase) && !limits.ContainsKey(key)))
            throw new InvalidOperationException("AiQueries contains unsupported settings.");
        foreach (var limit in limits)
            if (supplied.Contains(limit.Key) && Leaf(root, limit.Key) != limit.Value) throw Invalid(limit.Key);

        StructuredQueryProviderProfile profile;
        try { profile = StructuredQueryProviderProfile.FromName(supplied.Contains("Provider") ? Leaf(root, "Provider") : "Groq"); }
        catch (ArgumentException) { throw Invalid("Provider"); }
        string path = CounterPath(Leaf(root, "BudgetCounterPath"), environment);
        string key = Leaf(root, "ApiKey");
        // Match the transport's Bearer-token grammar; accepting a shape does not verify account access.
        if (key.Any(value => !char.IsAsciiLetterOrDigit(value) && !"-._~+/=".Contains(value))) throw Invalid("ApiKey");
        return new(true, profile, key, path);
    }

    /// <summary>Reads one required nonempty leaf without returning its value in an exception.</summary>
    private static string Leaf(IConfigurationSection root, string key)
    {
        var leaf = root.GetSection(key);
        string? value = leaf.Value;
        if (leaf.GetChildren().Any() || string.IsNullOrWhiteSpace(value) || value != value.Trim()) throw Invalid(key);
        return value;
    }

    /// <summary>Checks absolute lexical location using actual host paths, without resolving links or accessing storage.</summary>
    private static string CounterPath(string value, IWebHostEnvironment? environment)
    {
        const string key = "BudgetCounterPath";
        try
        {
            if (environment is null || value.Any(char.IsControl) || !Path.IsPathFullyQualified(value)
                || value.StartsWith(@"\\", StringComparison.Ordinal) || Path.EndsInDirectorySeparator(value)) throw Invalid(key);
            string webRoot = string.IsNullOrEmpty(environment.WebRootPath)
                ? Path.Combine(environment.ContentRootPath, "wwwroot") : environment.WebRootPath;
            if (!Path.IsPathFullyQualified(webRoot)) throw Invalid(key);
            string path = Path.GetFullPath(value);
            string relative = Path.GetRelativePath(Path.GetFullPath(webRoot), path);
            if (!Path.IsPathRooted(relative) && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw Invalid(key);
            return path;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw Invalid(key); }
    }

    /// <summary>Creates an owned key-name-only diagnostic, without inner configuration or filesystem details.</summary>
    private static InvalidOperationException Invalid(string key) =>
        new($"AiQueries:{key} is invalid or unsupported.");
}