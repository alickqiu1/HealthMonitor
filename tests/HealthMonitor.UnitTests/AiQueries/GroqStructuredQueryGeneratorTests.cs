using System.Net;
using System.Text;
using System.Text.Json;
using HealthMonitor.Application.AiQueries;
using HealthMonitor.Infrastructure.AiQueries;

namespace HealthMonitor.UnitTests.AiQueries;

/// <summary>Controls all transport I/O and time explicitly; no credential store, socket, or provider is used.</summary>
public sealed class GroqStructuredQueryGeneratorTests
{
    private const string Canary = "private-transport-canary";
    private static readonly QueryVocabulary Vocabulary = new(["DEN"], ["Server"]);
    private static byte[] Payload(string? q = null) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        model = GroqQueryProtocol.Model,
        choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = JsonSerializer.Serialize(new
        { outcome = "Ready", query = new { q, site = (string?)null, type = (string?)null, status = (string?)null, sort = "displayName", direction = "asc" }, reasonCode = "ready" }) } } }
    });

    [Fact]
    public void HandlerPolicy_RetainsNormalTlsAndDisablesRedirectProxyCookieAndDecompression()
    {
        using var handler = GroqStructuredQueryGenerator.CreateHandler();
        Assert.False(handler.AllowAutoRedirect); Assert.False(handler.UseProxy); Assert.False(handler.UseCookies);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression); Assert.Equal(0, handler.MaxResponseDrainSize);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")] [InlineData("secret\r\nheader")] [InlineData("secret,value")]
    public void InvalidCredential_IsRejectedWithoutEchoOrSend(string? key)
    {
        using var handler = new Handler();
        var error = Assert.Throws<ArgumentException>(() => new GroqStructuredQueryGenerator(key!, handler, new Clock()));
        Assert.Equal("apiKey", error.ParamName); Assert.Equal(0, handler.Calls);
        Assert.DoesNotContain("secret", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidRequest_SendsOnceToFixedOriginAndDisposesOwnedResources()
    {
        var body = new Body(Payload()); using var response = Response(body, known: true);
        var handler = new Handler { Operation = (_, _) => Task.FromResult(response) };
        using (var generator = new GroqStructuredQueryGenerator(Canary, handler, new Clock()))
        {
            var result = await generator.GenerateAsync("devices", Vocabulary, default);
            Assert.Equal("Ready", result!.Outcome); Assert.Equal(1, handler.Calls);
            Assert.Equal(GroqQueryProtocol.Endpoint, handler.Uri); Assert.Equal("Bearer " + Canary, handler.Authorization);
            Assert.DoesNotContain(Canary, handler.RequestBody!); Assert.True(body.Disposed); Assert.False(body.DisposedWhileReading);
            using var json = JsonDocument.Parse(handler.RequestBody!); Assert.Equal(400, json.RootElement.GetProperty("max_completion_tokens").GetInt32());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => handler.Request!.Content!.ReadAsStringAsync());
        }
        Assert.True(handler.Disposed);
    }

    [Theory]
    [InlineData(302)] [InlineData(401)] [InlineData(429)] [InlineData(500)]
    public async Task NonSuccess_NeverReadsErrorBodyOrRetries(int status)
    {
        var body = new Body(Encoding.UTF8.GetBytes(Canary)) { ReadOperation = (_, _) => throw new IOException(Canary) };
        using var response = Response(body); response.StatusCode = (HttpStatusCode)status;
        using var handler = new Handler { Operation = (_, _) => Task.FromResult(response) };
        using var generator = new GroqStructuredQueryGenerator(Canary, handler, new Clock());
        await SafeFailure<AiProviderUnavailableException>(() => generator.GenerateAsync("devices", Vocabulary, default));
        Assert.Equal(0, body.Reads); Assert.Equal(1, handler.Calls); Assert.True(body.Disposed);
    }

    [Fact]
    public async Task InvalidOrPreCancelledPrompt_DoesNotSend()
    {
        using var handler = new Handler(); using var generator = new GroqStructuredQueryGenerator(Canary, handler, new Clock());
        await Assert.ThrowsAsync<InvalidAiPromptException>(() => generator.GenerateAsync(" ", Vocabulary, default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generator.GenerateAsync("devices", Vocabulary, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken); Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task HeaderBudget_UsesTenSecondsWithoutSleeping()
    {
        var clock = new Clock(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler { Operation = async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException(); } };
        using var generator = new GroqStructuredQueryGenerator(Canary, handler, clock);
        var task = generator.GenerateAsync("devices", Vocabulary, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); clock.Advance(TimeSpan.FromSeconds(9)); Assert.False(task.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1)); await SafeFailure<AiProviderTimeoutException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task BodyBudget_IsSameBudgetAsHeadersAndWaitsForReadCompletion()
    {
        var clock = new Clock(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = new Body(Payload()) { ReadOperation = async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0; } };
        using var response = Response(body);
        using var handler = new Handler { Operation = (_, _) => { clock.Advance(TimeSpan.FromSeconds(6)); return Task.FromResult(response); } };
        using var generator = new GroqStructuredQueryGenerator(Canary, handler, clock);
        var task = generator.GenerateAsync("devices", Vocabulary, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); clock.Advance(TimeSpan.FromSeconds(3)); Assert.False(task.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1)); await SafeFailure<AiProviderTimeoutException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(body.Disposed); Assert.False(body.DisposedWhileReading);
    }

    [Fact]
    public async Task CallerCancellation_WinsBothBudgetsAndDoesNotAbandonUnfinishedSend()
    {
        var clock = new Clock(); using var caller = new CancellationTokenSource();
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler { Operation = (_, _) => pending.Task };
        using var generator = new GroqStructuredQueryGenerator(Canary, handler, clock);
        var task = generator.GenerateAsync("devices", Vocabulary, caller.Token);
        clock.Advance(TimeSpan.FromSeconds(10)); caller.Cancel();
        Assert.False(task.IsCompleted); Assert.False(handler.Disposed);
        pending.SetException(new HttpRequestException(Canary));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(caller.Token, error.CancellationToken); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ForeignCancellationAndExpectedNetworkFailures_AreUnavailable()
    {
        foreach (var failure in new Exception[] { new HttpRequestException(Canary), new IOException(Canary), new OperationCanceledException(Canary) })
        {
            using var handler = new Handler { Operation = (_, _) => Task.FromException<HttpResponseMessage>(failure) };
            using var generator = new GroqStructuredQueryGenerator(Canary, handler, new Clock());
            await SafeFailure<AiProviderUnavailableException>(() => generator.GenerateAsync("devices", Vocabulary, default));
            Assert.Equal(1, handler.Calls);
        }
        var body = new Body(Payload()) { ReadOperation = (_, _) => throw new IOException(Canary) };
        using var response = Response(body); using var bodyHandler = new Handler { Operation = (_, _) => Task.FromResult(response) };
        using var bodyGenerator = new GroqStructuredQueryGenerator(Canary, bodyHandler, new Clock());
        await SafeFailure<AiProviderUnavailableException>(() => bodyGenerator.GenerateAsync("devices", Vocabulary, default)); Assert.True(body.Disposed);
    }

    [Fact]
    public async Task BodyLimits_ProbeOnlyOneExtraByteAndValidateDeclaredLength()
    {
        var valid = Payload(); var exact = valid.Concat(new byte[8192 - valid.Length].Select(_ => (byte)' ')).ToArray();
        foreach (bool known in new[] { false, true })
        foreach (int size in new[] { 8192, 8193 })
        {
            var bytes = size == 8192 ? exact : exact.Concat(new byte[] { 0x20 }).ToArray(); var body = new Body(bytes);
            using var response = Response(body, known); using var handler = new Handler { Operation = (_, _) => Task.FromResult(response) };
            using var generator = new GroqStructuredQueryGenerator(Canary, handler, new Clock());
            if (size == 8192) Assert.NotNull(await generator.GenerateAsync("devices", Vocabulary, default));
            else await SafeFailure<InvalidStructuredQueryException>(() => generator.GenerateAsync("devices", Vocabulary, default));
            Assert.True(body.BytesRead <= 8193); if (known && size > 8192) Assert.Equal(0, body.Reads);
        }
        foreach (long length in new long[] { 0, valid.Length - 1, valid.Length + 1 })
        {
            var body = new Body(valid); using var response = Response(body); response.Content.Headers.ContentLength = length;
            using var handler = new Handler { Operation = (_, _) => Task.FromResult(response) }; using var generator = new GroqStructuredQueryGenerator(Canary, handler, new Clock());
            await SafeFailure<InvalidStructuredQueryException>(() => generator.GenerateAsync("devices", Vocabulary, default));
        }
    }

    [Fact]
    public async Task InvalidMediaEncodingAndCharset_RejectBeforeReading()
    {
        foreach (var media in new[] { "text/html", "application/json; charset=utf-16", "application/json; charset=utf-8; charset=utf-16", "application/json; charset=" + Canary })
        {
            var body = new Body(Payload()); using var response = Response(body); response.Content.Headers.Remove("Content-Type");
            response.Content.Headers.TryAddWithoutValidation("Content-Type", media);
            using var handler = new Handler { Operation = (_, _) => Task.FromResult(response) }; using var generator = new GroqStructuredQueryGenerator(Canary, handler, new Clock());
            await SafeFailure<InvalidStructuredQueryException>(() => generator.GenerateAsync("devices", Vocabulary, default)); Assert.Equal(0, body.Reads);
        }
        foreach (var encoding in new[] { "gzip", "identity" })
        {
            var body = new Body(Payload()); using var response = Response(body); response.Content.Headers.ContentEncoding.Add(encoding);
            using var handler = new Handler { Operation = (_, _) => Task.FromResult(response) }; using var generator = new GroqStructuredQueryGenerator(Canary, handler, new Clock());
            await SafeFailure<InvalidStructuredQueryException>(() => generator.GenerateAsync("devices", Vocabulary, default)); Assert.Equal(0, body.Reads);
        }
        foreach (var media in new[] { "application/json", "application/json; charset=\"UtF-8\"" })
        {
            using var response = Response(new Body(Payload())); response.Content.Headers.Remove("Content-Type"); response.Content.Headers.TryAddWithoutValidation("Content-Type", media);
            using var handler = new Handler { Operation = (_, _) => Task.FromResult(response) }; using var generator = new GroqStructuredQueryGenerator(Canary, handler, new Clock());
            Assert.NotNull(await generator.GenerateAsync("devices", Vocabulary, default));
        }
    }

    [Fact]
    public async Task CleanupFailure_DoesNotMaskPrimaryOrEscapeWithContent()
    {
        foreach (bool invalid in new[] { false, true })
        {
            var body = new Body(invalid ? Encoding.UTF8.GetBytes(Canary) : Payload()) { ThrowOnDispose = true };
            var response = Response(body); using var handler = new Handler { Operation = (_, _) => Task.FromResult(response) };
            using var generator = new GroqStructuredQueryGenerator(Canary, handler, new Clock());
            if (invalid) await SafeFailure<InvalidStructuredQueryException>(() => generator.GenerateAsync("devices", Vocabulary, default));
            else await SafeFailure<AiProviderUnavailableException>(() => generator.GenerateAsync("devices", Vocabulary, default));
            Assert.True(body.Disposed);
        }
        using var caller = new CancellationTokenSource();
        var cancelledBody = new Body(Payload()) { OnDispose = () => caller.Cancel(), ThrowOnDispose = true };
        var cancelledResponse = Response(cancelledBody); using var cancelledHandler = new Handler { Operation = (_, _) => Task.FromResult(cancelledResponse) };
        using var cancelledGenerator = new GroqStructuredQueryGenerator(Canary, cancelledHandler, new Clock());
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledGenerator.GenerateAsync("devices", Vocabulary, caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
    }

    [Fact]
    public async Task ConcurrentCalls_KeepPerRequestStateAndBudgetSeparate()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int entered = 0;
        using var handler = new Handler { Operation = async (request, token) =>
        {
            if (Interlocked.Increment(ref entered) == 2) both.TrySetResult();
            await release.Task.WaitAsync(token);
            Assert.Equal("Bearer " + Canary, request.Headers.Authorization?.ToString());
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            return Response(new Body(Payload(json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString())));
        } };
        using var generator = new GroqStructuredQueryGenerator(Canary, handler, new Clock());
        var first = generator.GenerateAsync("first", Vocabulary, default); var second = generator.GenerateAsync("second", Vocabulary, default);
        await both.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(first.IsCompleted); Assert.False(second.IsCompleted); release.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "first", "second" }, results.Select(result => result!.Query!.Q)); Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task CancellingOneConcurrentCall_DoesNotCancelTheOther()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int entered = 0;
        using var handler = new Handler { Operation = async (_, token) =>
        { if (Interlocked.Increment(ref entered) == 2) both.TrySetResult(); await release.Task.WaitAsync(token); return Response(new Body(Payload())); } };
        using var generator = new GroqStructuredQueryGenerator(Canary, handler, new Clock()); using var caller = new CancellationTokenSource();
        var first = generator.GenerateAsync("cancel", Vocabulary, caller.Token); var second = generator.GenerateAsync("keep", Vocabulary, default);
        await both.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(caller.Token, error.CancellationToken); Assert.False(second.IsCompleted); release.SetResult();
        Assert.NotNull(await second.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task CallerAbortDuringBodyRead_PreservesTokenAndWaitsForReadTermination()
    {
        foreach (bool simultaneousBudget in new[] { false, true })
        {
            var clock = new Clock(); using var caller = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken readToken = default;
            var body = new Body(Payload()) { ReadOperation = (_, token) => { readToken = token; entered.TrySetResult(); return new(pending.Task); } };
            using var response = Response(body); using var handler = new Handler { Operation = (_, _) => Task.FromResult(response) };
            using var generator = new GroqStructuredQueryGenerator(Canary, handler, clock);
            var task = generator.GenerateAsync("devices", Vocabulary, caller.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel();
            if (simultaneousBudget) clock.Advance(TimeSpan.FromSeconds(10));
            // Cancellation alone cannot release a stream whose read has not terminated yet.
            Assert.False(task.IsCompleted); Assert.False(body.Disposed); pending.SetCanceled(readToken);
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(caller.Token, error.CancellationToken); Assert.True(body.Disposed); Assert.False(body.DisposedWhileReading); Assert.Equal(1, handler.Calls);
        }
    }

    [Fact]
    public async Task LateValidHeaders_AfterOwnedBudgetAreRejectedWithoutReadingBody()
    {
        var clock = new Clock(); var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = new Body(Payload()); using var response = Response(body);
        using var handler = new Handler { Operation = (_, _) => pending.Task }; using var generator = new GroqStructuredQueryGenerator(Canary, handler, clock);
        var task = generator.GenerateAsync("devices", Vocabulary, default); clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(task.IsCompleted); pending.SetResult(response);
        await SafeFailure<AiProviderTimeoutException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, body.Reads); Assert.True(body.Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>Requires content-free expected failures and no attached provider/parser diagnostic.</summary>
    private static async Task SafeFailure<T>(Func<Task> operation) where T : Exception
    { var error = await Assert.ThrowsAsync<T>(operation); Assert.DoesNotContain(Canary, error.Message); Assert.Null(error.InnerException); }
    /// <summary>Constructs only synthetic HTTP metadata around a tracked, non-seekable body.</summary>
    private static HttpResponseMessage Response(Body body, bool known = false)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        response.Content.Headers.ContentType = new("application/json"); if (known) response.Content.Headers.ContentLength = body.Length;
        return response;
    }
    /// <summary>Records a send before invoking test-controlled header completion; it never opens a socket.</summary>
    private sealed class Handler : HttpMessageHandler
    {
        internal int Calls; internal bool Disposed; internal Uri? Uri; internal string? Authorization, RequestBody; internal HttpRequestMessage? Request;
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Operation = (_, _) => Task.FromResult(Response(new Body(Payload())));
        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Calls); Request = request; Uri = request.RequestUri; Authorization = request.Headers.Authorization?.ToString(); RequestBody = await request.Content!.ReadAsStringAsync(token); return await Operation(request, token); }
        /// <inheritdoc />
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    /// <summary>Tracks actual byte reads and teardown, including cooperative cancellation and deliberately failing disposal.</summary>
    private sealed class Body(byte[] bytes) : MemoryStream(bytes)
    {
        internal Func<Memory<byte>, CancellationToken, ValueTask<int>>? ReadOperation; internal Action? OnDispose;
        internal int Reads, BytesRead, active; internal bool Disposed, DisposedWhileReading, ThrowOnDispose;
        /// <inheritdoc />
        public override bool CanSeek => false;
        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { Reads++; Interlocked.Increment(ref active); try { int count = ReadOperation is null ? await base.ReadAsync(buffer, token) : await ReadOperation(buffer, token); BytesRead += count; return count; } finally { Interlocked.Decrement(ref active); } }
        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        { DisposedWhileReading |= active != 0; Disposed = true; base.Dispose(disposing); OnDispose?.Invoke(); if (ThrowOnDispose) throw new IOException(Canary); }
    }
    /// <summary>Advances only the one-shot timers used by CancellationTokenSource; callbacks fire outside the clock lock.</summary>
    private sealed class Clock : TimeProvider
    {
        private readonly object gate = new(); private DateTimeOffset now = DateTimeOffset.UnixEpoch; private readonly List<Timer> timers = new();
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() { lock (gate) return now; }
        /// <inheritdoc />
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new Timer(this, callback, state); lock (gate) { timer.Change(dueTime, period); timers.Add(timer); } return timer; }
        /// <summary>Triggers due budgets deterministically without sleeping or holding a lock during cancellation callbacks.</summary>
        internal void Advance(TimeSpan elapsed)
        { Timer[] ready; lock (gate) { now += elapsed; ready = timers.Where(timer => !timer.Disposed && timer.Due <= now).ToArray(); foreach (var timer in ready) timer.Due = DateTimeOffset.MaxValue; } foreach (var timer in ready) timer.Callback(timer.State); }
        /// <summary>Models only the owned one-shot cancellation timer, with idempotent disposal.</summary>
        private sealed class Timer(Clock owner, TimerCallback callback, object? state) : ITimer
        {
            internal readonly TimerCallback Callback = callback; internal readonly object? State = state; internal bool Disposed; internal DateTimeOffset Due = DateTimeOffset.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Only owned one-shot budgets are supported."); lock (owner.gate) { if (Disposed) return false; Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner.now + dueTime; return true; } }
            public void Dispose() { lock (owner.gate) Disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
