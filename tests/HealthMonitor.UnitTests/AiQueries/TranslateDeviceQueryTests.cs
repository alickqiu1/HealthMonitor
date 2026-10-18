using HealthMonitor.Application.AiQueries;
using HealthMonitor.Application.AiQueries.Ports;

namespace HealthMonitor.UnitTests.AiQueries;

/// <summary>Verifies single-call orchestration, prompt boundaries, and cooperative cancellation without a provider.</summary>
public sealed class TranslateDeviceQueryTests
{
    private static readonly QueryVocabulary Vocabulary = new(["DEN"], ["Server"]);
    private static readonly StructuredQueryProposal Ready = new("Ready",
        new(null, null, null, null, "displayName", "asc"), "ready");

    [Fact]
    public void Construction_RejectsMissingOwnedDependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new TranslateDeviceQuery(null!, Vocabulary));
        Assert.Throws<ArgumentNullException>(() => new TranslateDeviceQuery(new Generator(), null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public async Task InvalidPrompt_IsRejectedBeforeGeneration(string? prompt)
    {
        var generator = new Generator();
        var error = await Assert.ThrowsAsync<InvalidAiPromptException>(() =>
            new TranslateDeviceQuery(generator, Vocabulary).TranslateAsync(prompt, default));
        Assert.Equal("The prompt must contain 1-500 characters after trimming.", error.Message);
        Assert.Equal(0, generator.Calls);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(500, true)]
    [InlineData(501, false)]
    public async Task PromptLength_IsCheckedAfterTrimming(int length, bool valid)
    {
        var generator = new Generator();
        var text = new string('x', length);
        var task = new TranslateDeviceQuery(generator, Vocabulary).TranslateAsync(" \t" + text + "\r\n ", default);
        if (valid)
        {
            Assert.Equal("Ready", (await task).Outcome);
            Assert.Equal(text, generator.Prompt);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidAiPromptException>(() => task);
            Assert.DoesNotContain(text, error.Message, StringComparison.Ordinal);
        }
        Assert.Equal(valid ? 1 : 0, generator.Calls);
    }

    [Fact]
    public async Task PromptLimit_UsesUtf16UnitsLikeTheExistingReadContract()
    {
        var generator = new Generator();
        var consumer = new TranslateDeviceQuery(generator, Vocabulary);
        await consumer.TranslateAsync(string.Concat(Enumerable.Repeat("😀", 250)), default);
        await Assert.ThrowsAsync<InvalidAiPromptException>(() =>
            consumer.TranslateAsync(string.Concat(Enumerable.Repeat("😀", 251)), default));
        Assert.Equal(1, generator.Calls);
    }

    [Theory]
    [InlineData("Ready", "ready")]
    [InlineData("NeedsClarification", "ambiguous")]
    [InlineData("NeedsClarification", "no_interpretation")]
    [InlineData("Unsupported", "unsupported")]
    public async Task ValidDecision_GeneratesOnceWithSameVocabularyAndToken(string outcome, string reason)
    {
        using var cancellation = new CancellationTokenSource();
        var proposal = new StructuredQueryProposal(outcome, outcome == "Ready" ? Ready.Query : null, reason);
        var generator = new Generator { Generate = (_, _, _) => Task.FromResult<StructuredQueryProposal?>(proposal) };
        var result = await new TranslateDeviceQuery(generator, Vocabulary).TranslateAsync("  delete offline  ", cancellation.Token);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(1, generator.Calls);
        Assert.Equal("delete offline", generator.Prompt);
        Assert.Same(Vocabulary, generator.Vocabulary);
        Assert.Equal(cancellation.Token, generator.Token);
        // Intent classification belongs to the generator; the consumer must not invent a keyword blacklist.
        Assert.Equal(outcome == "Ready", result.Query is not null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidProposal_IsRejectedWithoutRetryOrContentEcho(bool absent)
    {
        var proposal = absent ? null : new StructuredQueryProposal("private-provider-output", Ready.Query, "ready");
        var generator = new Generator { Generate = (_, _, _) => Task.FromResult(proposal) };
        var error = await Assert.ThrowsAsync<InvalidStructuredQueryException>(() =>
            new TranslateDeviceQuery(generator, Vocabulary).TranslateAsync("private-user-prompt", default));
        Assert.Equal("The generated query proposal is invalid.", error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(1, generator.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderFailure_PropagatesOriginalInstanceWithoutRetry(bool synchronous)
    {
        var original = new InvalidOperationException("provider sentinel");
        var generator = new Generator
        {
            Generate = (_, _, _) => synchronous ? throw original : Task.FromException<StructuredQueryProposal?>(original)
        };
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TranslateDeviceQuery(generator, Vocabulary).TranslateAsync("valid prompt", default));
        Assert.Same(original, actual);
        Assert.Equal(1, generator.Calls);
    }

    [Fact]
    public async Task PreCancelledRequest_DoesNotValidateOrCallGenerator()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var generator = new Generator();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new TranslateDeviceQuery(generator, Vocabulary).TranslateAsync(null, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, generator.Calls);
    }

    [Fact]
    public async Task CancellationWhilePending_IsForwardedWithoutRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<StructuredQueryProposal?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new Generator { Generate = (_, _, token) => pending.Task.WaitAsync(token) };
        var task = new TranslateDeviceQuery(generator, Vocabulary).TranslateAsync("valid prompt", cancellation.Token);
        Assert.False(task.IsCompleted);
        cancellation.Cancel();
        // Bound the assertion so a regression that loses cancellation cannot hang the unit suite.
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, generator.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderIgnoringCancellation_CannotPublishDecisionOrInvalidOutputFailure(bool invalid)
    {
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<StructuredQueryProposal?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new Generator { Generate = (_, _, _) => pending.Task };
        var task = new TranslateDeviceQuery(generator, Vocabulary).TranslateAsync("valid prompt", cancellation.Token);
        cancellation.Cancel();
        // The consumer cannot terminate an uncooperative provider; it rejects its eventual completion instead.
        Assert.False(task.IsCompleted);
        pending.SetResult(invalid ? null : Ready);
        // Bound the assertion so a regression that loses cancellation cannot hang the unit suite.
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, generator.Calls);
    }

    /// <summary>Records the one port invocation and lets each test control completion or failure explicitly.</summary>
    private sealed class Generator : IStructuredQueryGenerator
    {
        internal Func<string, QueryVocabulary, CancellationToken, Task<StructuredQueryProposal?>> Generate { get; init; }
            = (_, _, _) => Task.FromResult<StructuredQueryProposal?>(Ready);
        internal int Calls { get; private set; }
        internal string? Prompt { get; private set; }
        internal QueryVocabulary? Vocabulary { get; private set; }
        internal CancellationToken Token { get; private set; }

        /// <summary>Captures supplied arguments before invoking the test-controlled operation.</summary>
        public Task<StructuredQueryProposal?> GenerateAsync(string prompt, QueryVocabulary vocabulary,
            CancellationToken cancellationToken)
        {
            Calls++;
            Prompt = prompt;
            Vocabulary = vocabulary;
            Token = cancellationToken;
            return Generate(prompt, vocabulary, cancellationToken);
        }
    }
}
