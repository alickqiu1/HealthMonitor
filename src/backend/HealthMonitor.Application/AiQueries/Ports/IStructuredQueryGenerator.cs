namespace HealthMonitor.Application.AiQueries.Ports;

/// <summary>Generates one untrusted filter proposal without exposing provider transport types.</summary>
/// <remarks>Implementations must strictly parse provider output and enforce bounded transport. Cancellation
/// forwarding alone cannot force an uncooperative provider to terminate or prevent provider billing.</remarks>
public interface IStructuredQueryGenerator
{
    /// <summary>Requests a proposal using separate user text and owned filter vocabulary.</summary>
    /// <param name="prompt">Already trimmed user text of 1-500 UTF-16 units; still untrusted instructions.</param>
    /// <param name="vocabulary">Immutable canonical seed vocabulary, with no storage access.</param>
    /// <param name="cancellationToken">Cancellation forwarded by the caller.</param>
    /// <returns>An untrusted proposal, or null if no structured proposal was produced.</returns>
    Task<StructuredQueryProposal?> GenerateAsync(string prompt, QueryVocabulary vocabulary,
        CancellationToken cancellationToken);
}
