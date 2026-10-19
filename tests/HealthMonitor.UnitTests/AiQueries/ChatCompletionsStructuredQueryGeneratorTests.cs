using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HealthMonitor.Application.AiQueries;
using HealthMonitor.Infrastructure.AiQueries;

namespace HealthMonitor.UnitTests.AiQueries;

/// <summary>Exercises both profiles through the shared HTTP boundary with controlled in-memory transport and time.</summary>
public sealed class ChatCompletionsStructuredQueryGeneratorTests
{
    private const string Key = "fictional-key-canary";
    private const string Prompt = "private-prompt-canary";
    private static readonly QueryVocabulary Vocabulary = new(["DEN"], ["Server"]);

    /// <summary>Creates a complete synthetic success response; HttpClient and the generator own its disposal.</summary>
    private static HttpResponseMessage Success(StructuredQueryProviderProfile profile) => new(HttpStatusCode.OK)
    { Content = new ByteArrayContent(ChatCompletionsQueryProtocolTests.Envelope(profile))
        { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } } };

    [Theory]
    [InlineData("Groq", "https://api.groq.com/openai/v1/chat/completions")]
    [InlineData("OpenAI", "https://api.openai.com/v1/chat/completions")]
    public async Task RealRequestBoundary_UsesExactTargetPerRequestBearerAndProfileWireFields(string name, string endpoint)
    {
        var profile = StructuredQueryProviderProfile.FromName(name);
        var handler = new ControlledHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal(endpoint, request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); Assert.Equal(Key, request.Headers.Authorization.Parameter);
            var bytes = await request.Content!.ReadAsByteArrayAsync(token);
            using var body = JsonDocument.Parse(bytes);
            Assert.DoesNotContain(Key, System.Text.Encoding.UTF8.GetString(bytes));
            Assert.Equal(Prompt, body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
            Assert.Equal(name == "Groq" ? "low" : "none", body.RootElement.GetProperty("reasoning_effort").GetString());
            Assert.Equal(profile.Model, body.RootElement.GetProperty("model").GetString());
            if (name == "OpenAI") { Assert.False(body.RootElement.GetProperty("store").GetBoolean()); Assert.False(body.RootElement.TryGetProperty("include_reasoning", out _)); }
            return Success(profile);
        });
        var generator = new ChatCompletionsStructuredQueryGenerator(profile, Key, handler, TimeProvider.System);
        Assert.Equal("Ready", (await generator.GenerateAsync(Prompt, Vocabulary, default))!.Outcome);
        Assert.Equal(1, handler.Calls);
        generator.Dispose(); generator.Dispose();
        Assert.Equal(1, handler.DisposeCount);
    }

    [Theory]
    [InlineData("Groq")] [InlineData("OpenAI")]
    public async Task ProviderFailure_DoesNotReadErrorBodyRetryOrExposePrivateMaterial(string name)
    {
        var content = new ForbiddenReadContent();
        var handler = new ControlledHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = content }));
        using var generator = new ChatCompletionsStructuredQueryGenerator(StructuredQueryProviderProfile.FromName(name), Key, handler, TimeProvider.System);
        var error = await Assert.ThrowsAsync<AiProviderUnavailableException>(() => generator.GenerateAsync(Prompt, Vocabulary, default));
        Assert.Equal(1, handler.Calls); Assert.False(content.ReadAttempted); Assert.True(content.Disposed);
        Assert.DoesNotContain(Key, error.ToString()); Assert.DoesNotContain(Prompt, error.ToString());
    }

    [Theory]
    [InlineData("Groq")] [InlineData("OpenAI")]
    public async Task OtherProfileResponse_FailsWithoutFallbackOrRetry(string name)
    {
        var profile = StructuredQueryProviderProfile.FromName(name);
        var other = StructuredQueryProviderProfile.FromName(name == "Groq" ? "OpenAI" : "Groq");
        var handler = new ControlledHandler((_, _) => Task.FromResult(Success(other)));
        using var generator = new ChatCompletionsStructuredQueryGenerator(profile, Key, handler, TimeProvider.System);
        await Assert.ThrowsAsync<InvalidStructuredQueryException>(() => generator.GenerateAsync(Prompt, Vocabulary, default));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("Groq", false)] [InlineData("OpenAI", false)]
    [InlineData("Groq", true)] [InlineData("OpenAI", true)]
    public async Task CancellationAndOwnedDeadline_ApplyToBothProfiles(string name, bool timeout)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new DeadlineClock();
        var handler = new ControlledHandler(async (_, token) =>
        {
            entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Controlled cancellation should have terminated the operation.");
        });
        using var caller = new CancellationTokenSource();
        using var generator = new ChatCompletionsStructuredQueryGenerator(StructuredQueryProviderProfile.FromName(name), Key, handler, clock);
        var pending = generator.GenerateAsync(Prompt, Vocabulary, caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (timeout) clock.Expire(); else caller.Cancel();
        if (timeout) await Assert.ThrowsAsync<AiProviderTimeoutException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        else
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(caller.Token, error.CancellationToken);
        }
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("Groq")] [InlineData("OpenAI")]
    public void InvalidKey_IsRejectedBeforeTransportUseWithoutEcho(string name)
    {
        using var handler = new ControlledHandler((_, _) => throw new InvalidOperationException("Must not send."));
        var error = Assert.Throws<ArgumentException>(() => new ChatCompletionsStructuredQueryGenerator(
            StructuredQueryProviderProfile.FromName(name), "private-key\r\nvalue", handler, TimeProvider.System));
        Assert.DoesNotContain("private-key", error.ToString()); Assert.Equal(0, handler.Calls);
        Assert.Throws<ArgumentNullException>(() => new ChatCompletionsStructuredQueryGenerator(null!, Key));
    }

    /// <summary>Captures actual HttpClient requests without opening sockets; disposal count detects duplicate client ownership.</summary>
    private sealed class ControlledHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> operation) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal int DisposeCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return operation(request, token); }
        protected override void Dispose(bool disposing) { if (disposing) DisposeCount++; base.Dispose(disposing); }
    }

    /// <summary>Proves non-success content is discarded without buffering or exposing provider diagnostics.</summary>
    private sealed class ForbiddenReadContent : HttpContent
    {
        internal bool ReadAttempted { get; private set; }
        internal bool Disposed { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { ReadAttempted = true; return Task.FromException(new InvalidOperationException(Key + Prompt)); }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    /// <summary>Controls only the transport's one-shot ten-second cancellation budget; no wall-clock sleeps are used.</summary>
    private sealed class DeadlineClock : TimeProvider
    {
        private BudgetTimer? timer;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromSeconds(10), dueTime); Assert.Equal(Timeout.InfiniteTimeSpan, period);
            timer = new BudgetTimer(callback, state); return timer;
        }
        internal void Expire() => timer!.Fire();

        /// <summary>Models a single cancellation callback with idempotent disposal, not a general-purpose scheduler.</summary>
        private sealed class BudgetTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            internal void Fire() { if (!disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
