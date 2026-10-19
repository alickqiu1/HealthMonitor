using HealthMonitor.Application.AiQueries;

namespace HealthMonitor.Infrastructure.AiQueries;

/// <summary>Preserves the original Groq protocol entry points over the shared, profile-validated implementation.</summary>
/// <remarks>The compatibility path always selects Groq; it cannot change endpoint or model through user text.</remarks>
internal static class GroqQueryProtocol
{
    private static readonly StructuredQueryProviderProfile Profile = StructuredQueryProviderProfile.FromName("Groq");
    /// <summary>Gets the original reviewed Groq model identity.</summary>
    internal static string Model => Profile.Model;
    /// <summary>Gets the original fixed Groq endpoint.</summary>
    internal static Uri Endpoint => Profile.Endpoint;

    /// <summary>Creates the unchanged Groq request policy without credentials or network access.</summary>
    /// <param name="prompt">Application-validated, trimmed user text.</param>
    /// <param name="vocabulary">Owned allowed filter tokens.</param>
    /// <returns>Caller-owned request bytes.</returns>
    internal static byte[] CreateRequestBody(string? prompt, QueryVocabulary vocabulary) =>
        ChatCompletionsQueryProtocol.CreateRequestBody(Profile, prompt, vocabulary);

    /// <summary>Strictly parses a bounded envelope using only the Groq profile's model identity.</summary>
    /// <param name="utf8">Complete bounded response bytes.</param>
    /// <returns>An untrusted proposal for subsequent Application validation.</returns>
    internal static StructuredQueryProposal ParseEnvelope(ReadOnlyMemory<byte> utf8) =>
        ChatCompletionsQueryProtocol.ParseEnvelope(Profile, utf8);
}
