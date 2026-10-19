using HealthMonitor.Application.AiQueries;
using HealthMonitor.Application.AiQueries.Ports;

namespace HealthMonitor.Infrastructure.AiQueries;

/// <summary>Preserves the original Groq transport API using the shared fixed-profile implementation.</summary>
/// <remarks>This wrapper owns exactly one core instance and forwards disposal; the core retains idempotent disposal. It adds no client, retry, configuration access or fallback.</remarks>
public sealed class GroqStructuredQueryGenerator : IStructuredQueryGenerator, IDisposable
{
    private static readonly StructuredQueryProviderProfile Profile = StructuredQueryProviderProfile.FromName("Groq");
    private readonly ChatCompletionsStructuredQueryGenerator inner;

    /// <summary>Creates the original Groq profile transport without opening a connection.</summary>
    /// <param name="apiKey">Header-safe backend credential; never logged or included in a request body.</param>
    /// <exception cref="ArgumentException">The credential cannot safely form a Bearer token.</exception>
    public GroqStructuredQueryGenerator(string apiKey) => inner = new(Profile, apiKey);

    /// <summary>Owns controlled test resources through the original internal-only seam.</summary>
    internal GroqStructuredQueryGenerator(string apiKey, HttpMessageHandler handler, TimeProvider clock) =>
        inner = new(Profile, apiKey, handler, clock);

    /// <summary>Retains the original fixed handler policy for network-free policy verification.</summary>
    internal static SocketsHttpHandler CreateHandler() => ChatCompletionsStructuredQueryGenerator.CreateHandler();

    /// <summary>Generates one Groq proposal with the original bounds, failure and cancellation policy.</summary>
    /// <param name="prompt">Already trimmed Application-validated user text.</param>
    /// <param name="vocabulary">The same immutable owned filter vocabulary.</param>
    /// <param name="cancellationToken">Caller cancellation forwarded without retry.</param>
    /// <returns>An untrusted proposal, requiring independent Application validation.</returns>
    /// <exception cref="InvalidAiPromptException">The prompt violates the bounded contract.</exception>
    /// <exception cref="InvalidStructuredQueryException">The provider output is invalid.</exception>
    /// <exception cref="AiProviderUnavailableException">The provider transport is unavailable.</exception>
    /// <exception cref="AiProviderTimeoutException">The owned provider deadline expired.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public Task<StructuredQueryProposal?> GenerateAsync(string prompt, QueryVocabulary vocabulary, CancellationToken cancellationToken) =>
        inner.GenerateAsync(prompt, vocabulary, cancellationToken);

    /// <summary>Disposes the one owned core after calls have drained; repeated disposal remains safe.</summary>
    public void Dispose() => inner.Dispose();
}
