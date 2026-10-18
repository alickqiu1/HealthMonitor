namespace HealthMonitor.Application.AiQueries;

/// <summary>Signals an invalid prompt without retaining or echoing its contents.</summary>
public sealed class InvalidAiPromptException : Exception
{
    /// <summary>Creates the fixed safe failure used for absent, blank, or oversized prompts.</summary>
    public InvalidAiPromptException() : base("The prompt must contain 1-500 characters after trimming.") { }
}
