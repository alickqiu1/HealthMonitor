namespace HealthMonitor.Infrastructure.AiQueries;

/// <summary>Identifies one reviewed structured-output provider/model and its fixed HTTPS endpoint.</summary>
/// <remarks>Profiles are installation choices, not arbitrary endpoint configuration. Private construction keeps
/// credentials from being redirected to an unreviewed host. This contract alone does not implement a transport.</remarks>
public sealed class StructuredQueryProviderProfile
{
    /// <summary>Gets the canonical installation configuration name.</summary>
    public string Name { get; }
    /// <summary>Gets the exact model identity required in requests and accepted responses.</summary>
    public string Model { get; }
    /// <summary>Gets the fixed complete Chat Completions endpoint for this profile.</summary>
    public Uri Endpoint { get; }

    private StructuredQueryProviderProfile(string name, string model, string endpoint)
    { Name = name; Model = model; Endpoint = new Uri(endpoint, UriKind.Absolute); }

    /// <summary>Resolves a supported profile without discovery, configuration access, credentials or network I/O.</summary>
    /// <param name="name">Groq or OpenAI, case-insensitive and without surrounding whitespace.</param>
    /// <returns>A detached immutable profile with reviewed endpoint and model identities.</returns>
    /// <exception cref="ArgumentException">The name is absent, malformed or unsupported; its value is never echoed.</exception>
    public static StructuredQueryProviderProfile FromName(string? name)
    {
        if (name is not null && name == name.Trim())
        {
            if (name.Equals("Groq", StringComparison.OrdinalIgnoreCase))
                return new("Groq", "openai/gpt-oss-20b", "https://api.groq.com/openai/v1/chat/completions");
            if (name.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
                return new("OpenAI", "gpt-5.4-mini-2026-03-17", "https://api.openai.com/v1/chat/completions");
        }
        throw new ArgumentException("A supported AI provider profile is required.", nameof(name));
    }
}