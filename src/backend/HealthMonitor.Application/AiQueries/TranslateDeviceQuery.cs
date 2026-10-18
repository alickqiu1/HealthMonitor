using HealthMonitor.Application.AiQueries.Ports;

namespace HealthMonitor.Application.AiQueries;

/// <summary>Coordinates one provider-neutral translation and local semantic validation.</summary>
/// <remarks>No storage or write use case is reachable here. HTTP admission controls, strict provider parsing,
/// and bounded transport remain separate responsibilities; this consumer does not retry or repair output.</remarks>
public sealed class TranslateDeviceQuery
{
    private readonly IStructuredQueryGenerator generator;
    private readonly QueryVocabulary vocabulary;

    /// <summary>Retains the generator and defensively snapshotted vocabulary owned by composition.</summary>
    /// <param name="generator">The single proposal-generation boundary.</param>
    /// <param name="vocabulary">Owned immutable seed vocabulary.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public TranslateDeviceQuery(IStructuredQueryGenerator generator, QueryVocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(vocabulary);
        this.generator = generator;
        this.vocabulary = vocabulary;
    }

    /// <summary>Validates the prompt, generates once, and returns a validated decision with fixed text.</summary>
    /// <param name="prompt">Untrusted user text; trimmed before checking the 1-500 UTF-16-unit limit.</param>
    /// <param name="cancellationToken">Cancellation forwarded unchanged to the generator.</param>
    /// <returns>A validated decision; never an arbitrary provider narrative.</returns>
    /// <exception cref="InvalidAiPromptException">The prompt is absent, blank, or oversized.</exception>
    /// <exception cref="InvalidStructuredQueryException">The returned proposal is absent or invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested before or after generation.</exception>
    /// <remarks>Provider failures propagate unchanged. Cancellation is cooperative and is not a billing guarantee.</remarks>
    public async Task<QueryTranslationResult> TranslateAsync(string? prompt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var trimmed = prompt?.Trim();
        if (trimmed is null || trimmed.Length is < 1 or > 500) throw new InvalidAiPromptException();
        var proposal = await generator.GenerateAsync(trimmed, vocabulary, cancellationToken);
        // A provider that returns after cancellation must not publish a now-obsolete translation.
        cancellationToken.ThrowIfCancellationRequested();
        return StructuredQueryValidator.Validate(proposal, vocabulary) ?? throw new InvalidStructuredQueryException();
    }
}
