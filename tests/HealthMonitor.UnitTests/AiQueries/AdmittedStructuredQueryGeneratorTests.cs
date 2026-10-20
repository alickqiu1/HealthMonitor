using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using HealthMonitor.Api.AiQueries;
using HealthMonitor.Application.AiQueries;
using HealthMonitor.Application.AiQueries.Ports;
using Microsoft.AspNetCore.Http;
using Xunit;
using static HealthMonitor.Api.AiQueries.AiAdmissionRejectedException;

namespace HealthMonitor.UnitTests.AiQueries;

/// <summary>Composes the real Application consumer and local controls with a fake provider and uniquely owned temporary counter.</summary>
public sealed class AdmittedStructuredQueryGeneratorTests
{
    [Fact]
    public async Task ValidSubmission_OneReservationAndExactProviderArguments()
    {
        await using var setup = new Setup();
        using var abort = new CancellationTokenSource();
        var result = await setup.Consumer().TranslateAsync("  devices  ", abort.Token);
        Assert.Equal("Ready", result.Outcome); Assert.Equal(1, setup.Count());
        var call = Assert.Single(setup.Provider.Calls);
        Assert.Equal("devices", call.Prompt); Assert.Same(setup.Vocabulary, call.Vocabulary); Assert.Equal(abort.Token, call.Token);
    }

    [Fact]
    public async Task InvalidApplicationPrompt_DoesNotReachDecoratorOrConsumeEitherBudget()
    {
        await using var setup = new Setup();
        foreach (string? prompt in new[] { null, "", " \t ", new string('x', 501) })
            await Assert.ThrowsAsync<InvalidAiPromptException>(() => setup.Consumer(configure: context => context.Features.Set<AiClientIdentity>(null)).TranslateAsync(prompt!, CancellationToken.None));
        Assert.Empty(setup.Provider.Calls); Assert.Equal(0, setup.Count());
        Assert.False(File.Exists(setup.Path + ".lock"));
        for (int index = 0; index < 5; index++) await setup.Consumer().TranslateAsync(new string('x', 500), CancellationToken.None);
        var denied = await Assert.ThrowsAsync<AiAdmissionRejectedException>(() => setup.Consumer().TranslateAsync("devices", CancellationToken.None));
        Assert.Equal(Rejection.RateLimited, denied.Reason); Assert.True(denied.RetryAfterSeconds >= 1);
        Assert.Equal(5, setup.Count()); Assert.Equal(5, setup.Provider.Calls.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0.0.0.0")]
    public async Task MissingTrustedIdentity_DoesNotTrustForwardingHeadersOrCharge(string? address)
    {
        await using var setup = new Setup();
        var error = await Assert.ThrowsAsync<AiAdmissionRejectedException>(() => setup.Consumer(address).TranslateAsync("devices", CancellationToken.None));
        Assert.Equal(Rejection.AdmissionUnavailable, error.Reason); Assert.Null(error.RetryAfterSeconds);
        Assert.Null(error.InnerException); Assert.Equal("AI admission was rejected.", error.Message);
        Assert.Empty(setup.Provider.Calls); Assert.Equal(0, setup.Count()); Assert.False(File.Exists(setup.Path + ".lock"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnverifiedMarker_RejectsBeforeAnyAdmissionEvenWithValidConnection(bool unavailable)
    {
        await using var setup = new Setup();
        var consumer = setup.Consumer(configure: context =>
            context.Features.Set<AiClientIdentity>(unavailable ? AiClientIdentity.Unavailable : null));
        var error = await Assert.ThrowsAsync<AiAdmissionRejectedException>(() => consumer.TranslateAsync("devices", CancellationToken.None));
        Assert.Equal(Rejection.AdmissionUnavailable, error.Reason); Assert.Null(error.RetryAfterSeconds);
        Assert.Empty(setup.Provider.Calls); Assert.Equal(0, setup.Count()); Assert.False(File.Exists(setup.Path + ".lock"));
        // Identity rejection must not consume a window token or leave a concurrency lease behind.
        for (int index = 0; index < 5; index++) await setup.Consumer().TranslateAsync("devices", CancellationToken.None);
        Assert.Equal(5, setup.Count()); Assert.Equal(5, setup.Provider.Calls.Count);
    }

    [Fact]
    public async Task VerifiedSnapshot_RemainsAuthoritativeAfterConnectionAndHeadersChange()
    {
        await using var setup = new Setup();
        for (int index = 1; index <= 6; index++)
        {
            var consumer = setup.Consumer(configure: context =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse($"198.51.100.{index}");
                context.Request.Headers["X-Forwarded-For"] = $"203.0.113.{index}";
            });
            if (index <= 5) await consumer.TranslateAsync("devices", CancellationToken.None);
            else
            {
                var error = await Assert.ThrowsAsync<AiAdmissionRejectedException>(() => consumer.TranslateAsync("devices", CancellationToken.None));
                Assert.Equal(Rejection.RateLimited, error.Reason);
            }
        }
        Assert.Equal(5, setup.Count()); Assert.Equal(5, setup.Provider.Calls.Count);
    }

    [Fact]
    public async Task CancelledCaller_WithMissingMarker_PreservesCancellationBeforeAdmission()
    {
        await using var setup = new Setup();
        using var abort = new CancellationTokenSource(); abort.Cancel();
        var consumer = setup.Consumer(configure: context => context.Features.Set<AiClientIdentity>(null));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer.TranslateAsync("devices", abort.Token));
        Assert.Equal(abort.Token, error.CancellationToken);
        Assert.Empty(setup.Provider.Calls); Assert.Equal(0, setup.Count()); Assert.False(File.Exists(setup.Path + ".lock"));
    }

    [Fact]
    public async Task MissingCounter_DisablesWithoutProviderOrRuntimeBootstrap()
    {
        await using var setup = new Setup(seed: false);
        var error = await Assert.ThrowsAsync<AiAdmissionRejectedException>(() => setup.Consumer().TranslateAsync("devices", CancellationToken.None));
        Assert.Equal(Rejection.BudgetUnavailable, error.Reason); Assert.Null(error.RetryAfterSeconds); Assert.Null(error.InnerException);
        Assert.Empty(setup.Provider.Calls); Assert.False(File.Exists(setup.Path));
        // Both slots are available after the daily gate fails; the failed attempt did not leak concurrency.
        using var first = setup.Policy.TryEnter(IPAddress.Parse("192.0.2.2"));
        using var second = setup.Policy.TryEnter(IPAddress.Parse("192.0.2.3"));
        Assert.Equal(AiAdmissionPolicy.Outcome.Acquired, first.Status); Assert.Equal(AiAdmissionPolicy.Outcome.Acquired, second.Status);
    }

    [Theory]
    [InlineData("2026-10-18T00:00:00Z", 86400)]
    [InlineData("2026-10-18T12:00:00Z", 43200)]
    [InlineData("2026-10-18T23:59:59.900Z", 1)]
    public async Task Exhaustion_UsesPositiveCeiledUtcMidnightRetry(string now, int seconds)
    {
        await using var setup = new Setup(count: 100);
        setup.Clock.Now = DateTimeOffset.Parse(now, CultureInfo.InvariantCulture);
        var error = await Assert.ThrowsAsync<AiAdmissionRejectedException>(() => setup.Consumer().TranslateAsync("devices", CancellationToken.None));
        Assert.Equal(Rejection.BudgetExhausted, error.Reason); Assert.Equal(seconds, error.RetryAfterSeconds);
        Assert.Equal(100, setup.Count()); Assert.Empty(setup.Provider.Calls);
    }

    [Fact]
    public async Task MidnightDuringExhaustion_DoesNotDelayRetryAnExtraDay()
    {
        await using var setup = new Setup(count: 100);
        var before = new DateTimeOffset(2026, 10, 18, 23, 59, 59, TimeSpan.Zero);
        setup.Clock.Readings.Enqueue(before); setup.Clock.Readings.Enqueue(before); setup.Clock.Readings.Enqueue(before.AddSeconds(1));
        var error = await Assert.ThrowsAsync<AiAdmissionRejectedException>(() => setup.Consumer().TranslateAsync("devices", CancellationToken.None));
        Assert.Equal(Rejection.BudgetExhausted, error.Reason); Assert.Equal(1, error.RetryAfterSeconds);
        Assert.Equal(100, setup.Count()); Assert.Empty(setup.Provider.Calls);
    }

    [Fact]
    public async Task ClockRollback_CounterFailureIsNotOrdinaryExhaustion()
    {
        await using var setup = new Setup(count: 100);
        setup.Clock.Readings.Enqueue(setup.Clock.Now); setup.Clock.Readings.Enqueue(setup.Clock.Now.AddDays(-1));
        var error = await Assert.ThrowsAsync<AiAdmissionRejectedException>(() => setup.Consumer().TranslateAsync("devices", CancellationToken.None));
        Assert.Equal(Rejection.BudgetUnavailable, error.Reason); Assert.Null(error.RetryAfterSeconds);
        Assert.Equal(100, setup.Count()); Assert.Empty(setup.Provider.Calls);
    }

    [Theory]
    [InlineData("unavailable", false)]
    [InlineData("unavailable", true)]
    [InlineData("timeout", false)]
    [InlineData("timeout", true)]
    [InlineData("unexpected", false)]
    [InlineData("unexpected", true)]
    [InlineData("foreign-cancellation", false)]
    [InlineData("foreign-cancellation", true)]
    public async Task ProviderFailure_IdentityAndCommittedCountPreservedWithoutRetry(string kind, bool synchronous)
    {
        await using var setup = new Setup();
        Exception failure = kind switch
        {
            "unavailable" => new AiProviderUnavailableException(), "timeout" => new AiProviderTimeoutException(),
            "foreign-cancellation" => new OperationCanceledException("private-provider-canary"),
            _ => new InvalidOperationException("private-provider-canary")
        };
        setup.Provider.Operation = (_, _, _) => synchronous ? throw failure : Task.FromException<StructuredQueryProposal?>(failure);
        var actual = await Assert.ThrowsAnyAsync<Exception>(() => setup.Consumer().TranslateAsync("devices", CancellationToken.None));
        Assert.Same(failure, actual); Assert.Equal(1, setup.Count()); Assert.Single(setup.Provider.Calls);
    }

    [Fact]
    public async Task InvalidSemanticProposal_StillConsumesOneCommittedAttempt()
    {
        await using var setup = new Setup();
        setup.Provider.Operation = (_, _, _) => Task.FromResult<StructuredQueryProposal?>(new("Ready",
            new(null, null, null, "invented", "displayName", "asc"), "ready"));
        await Assert.ThrowsAsync<InvalidStructuredQueryException>(() => setup.Consumer().TranslateAsync("devices", CancellationToken.None));
        Assert.Equal(1, setup.Count()); Assert.Single(setup.Provider.Calls);
    }

    [Fact]
    public async Task PreCancellation_DoesNotChargeOrInvokeProvider()
    {
        await using var setup = new Setup();
        using var abort = new CancellationTokenSource(); abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.Consumer().TranslateAsync("devices", abort.Token));
        Assert.Equal(0, setup.Count()); Assert.Empty(setup.Provider.Calls); Assert.False(File.Exists(setup.Path + ".lock"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncooperativeCancellation_HoldsCapacityUntilProviderDrainsAndNeverRefunds(bool providerFails)
    {
        await using var setup = new Setup();
        var firstGate = new TaskCompletionSource<StructuredQueryProposal?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondGate = new TaskCompletionSource<StructuredQueryProposal?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entries = 0;
        setup.Provider.Operation = (_, _, _) =>
        {
            int number = Interlocked.Increment(ref entries);
            if (number == 1) firstEntered.TrySetResult();
            if (number == 2) entered.TrySetResult();
            return number switch { 1 => firstGate.Task, 2 => secondGate.Task, _ => Task.FromResult<StructuredQueryProposal?>(Ready()) };
        };
        using var abort = new CancellationTokenSource();
        var first = setup.Consumer("192.0.2.1").TranslateAsync("devices", abort.Token);
        Task<QueryTranslationResult>? second = null;
        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = setup.Consumer("192.0.2.2").TranslateAsync("devices", CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); abort.Cancel();
            Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
            var rejected = await Assert.ThrowsAsync<AiAdmissionRejectedException>(() => setup.Consumer("192.0.2.3").TranslateAsync("devices", CancellationToken.None));
            Assert.Equal(Rejection.RateLimited, rejected.Reason); Assert.Equal(2, setup.Count()); Assert.Equal(2, entries);
            if (providerFails) firstGate.TrySetException(new AiProviderUnavailableException());
            else firstGate.TrySetResult(Ready());
            var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(abort.Token, cancelled.CancellationToken);
            await setup.Consumer("192.0.2.4").TranslateAsync("devices", CancellationToken.None);
            Assert.Equal(3, setup.Count()); Assert.Equal(3, entries); Assert.False(second.IsCompleted);
        }
        finally
        {
            // Cleanup cannot race unresolved provider work: open both controlled gates, then independently drain each request.
            firstGate.TrySetResult(Ready()); secondGate.TrySetResult(Ready());
            try { await first.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) when (abort.IsCancellationRequested) { }
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task CancellationRacingBudgetExhaustion_PreservesCallerTokenAndNeverInvokesProvider()
    {
        await using var setup = new Setup(count: 100);
        using var abort = new CancellationTokenSource();
        int reads = 0;
        setup.Clock.Observe = () => { if (Interlocked.Increment(ref reads) == 2) abort.Cancel(); };
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.Consumer().TranslateAsync("devices", abort.Token));
        Assert.Equal(abort.Token, cancelled.CancellationToken); Assert.Equal(100, setup.Count()); Assert.Empty(setup.Provider.Calls);
        using var first = setup.Policy.TryEnter(IPAddress.Parse("192.0.2.2"));
        using var second = setup.Policy.TryEnter(IPAddress.Parse("192.0.2.3"));
        Assert.Equal(AiAdmissionPolicy.Outcome.Acquired, first.Status); Assert.Equal(AiAdmissionPolicy.Outcome.Acquired, second.Status);
    }

    [Fact]
    public async Task Constructor_RejectsNullDependencies()
    {
        await using var setup = new Setup(); var context = new DefaultHttpContext();
        Assert.Throws<ArgumentNullException>(() => new AdmittedStructuredQueryGenerator(null!, setup.Policy, setup.Budget, context, setup.Clock));
        Assert.Throws<ArgumentNullException>(() => new AdmittedStructuredQueryGenerator(setup.Provider, null!, setup.Budget, context, setup.Clock));
        Assert.Throws<ArgumentNullException>(() => new AdmittedStructuredQueryGenerator(setup.Provider, setup.Policy, null!, context, setup.Clock));
        Assert.Throws<ArgumentNullException>(() => new AdmittedStructuredQueryGenerator(setup.Provider, setup.Policy, setup.Budget, null!, setup.Clock));
        Assert.Throws<ArgumentNullException>(() => new AdmittedStructuredQueryGenerator(setup.Provider, setup.Policy, setup.Budget, context, null!));
    }

    [Fact]
    public void RejectionMetadata_IsOwnedAndBounded()
    {
        foreach (int invalid in new[] { 0, -1, 86401 })
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiAdmissionRejectedException(Rejection.RateLimited, invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiAdmissionRejectedException((Rejection)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiAdmissionRejectedException(Rejection.BudgetExhausted));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiAdmissionRejectedException(Rejection.BudgetUnavailable, 1));
        var bounded = new AiAdmissionRejectedException(Rejection.BudgetExhausted, 86400);
        Assert.Equal(86400, bounded.RetryAfterSeconds); Assert.Null(bounded.InnerException);
        Assert.Equal("AI admission was rejected.", bounded.Message);
    }

    /// <summary>Produces an untrusted but semantically valid candidate; no model interpretation is claimed.</summary>
    private static StructuredQueryProposal Ready() => new("Ready", new(null, null, null, null, "displayName", "asc"), "ready");

    /// <summary>Records exact port arguments and supports explicitly controlled provider failures and completion gates.</summary>
    private sealed class Provider : IStructuredQueryGenerator
    {
        internal ConcurrentQueue<(string Prompt, QueryVocabulary Vocabulary, CancellationToken Token)> Calls { get; } = new();
        internal Func<string, QueryVocabulary, CancellationToken, Task<StructuredQueryProposal?>> Operation { get; set; }
            = (_, _, _) => Task.FromResult<StructuredQueryProposal?>(Ready());
        /// <inheritdoc />
        public Task<StructuredQueryProposal?> GenerateAsync(string prompt, QueryVocabulary vocabulary, CancellationToken cancellationToken)
        { Calls.Enqueue((prompt, vocabulary, cancellationToken)); return Operation(prompt, vocabulary, cancellationToken); }
    }

    /// <summary>Supplies one shared UTC clock, with an optional deterministic sequence for transitions within a reservation.</summary>
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        internal ConcurrentQueue<DateTimeOffset> Readings { get; } = new();
        /// <summary>Allows one test to cancel at the budget clock observation without adding a production persistence hook.</summary>
        internal Action? Observe { get; set; }
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow()
        { Observe?.Invoke(); return Readings.TryDequeue(out var next) ? next : Now; }
    }

    /// <summary>Owns only a unique test directory and borrowed-dependency composition; runtime provisioning and DI remain unimplemented.</summary>
    private sealed class Setup : IAsyncDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HealthMonitor-admission-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(root, "counter.json");
        internal Clock Clock { get; } = new();
        internal Provider Provider { get; } = new();
        internal AiAdmissionPolicy Policy { get; } = new();
        internal AiDailyBudgetStore Budget { get; }
        internal QueryVocabulary Vocabulary { get; } = new(["DEN"], ["Server"]);
        /// <summary>Explicitly seeds controlled initial state when requested; absent state tests fail-closed bootstrap.</summary>
        internal Setup(bool seed = true, int count = 0)
        {
            Directory.CreateDirectory(root);
            if (seed) File.WriteAllText(Path, JsonSerializer.Serialize(new { version = 1, day = "2026-10-18", count }));
            Budget = new(Path, Clock);
        }
        /// <summary>Composes the real validator with explicitly synthetic ingress metadata; this is not real transport verification.</summary>
        /// <param name="address">Controlled test peer copied into both connection metadata and the owned identity feature.</param>
        /// <param name="configure">Optional mutation after capture, allowing missing-marker and later-rewrite regressions.</param>
        /// <returns>The Application consumer over real local admission controls and the fake provider.</returns>
        internal TranslateDeviceQuery Consumer(string? address = "192.0.2.1", Action<DefaultHttpContext>? configure = null)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = address is null ? null : IPAddress.Parse(address);
            context.Features.Set(AiClientIdentity.Capture(context.Connection.RemoteIpAddress));
            context.Request.Headers["X-Forwarded-For"] = "198.51.100.42";
            configure?.Invoke(context);
            return new(new AdmittedStructuredQueryGenerator(Provider, Policy, Budget, context, Clock), Vocabulary);
        }
        /// <summary>Drains are the test's responsibility before disposal; verifies the resolved unique temporary target before recursive deletion.</summary>
        public async ValueTask DisposeAsync()
        {
            await Budget.DisposeAsync(); Policy.Dispose();
            string resolved = System.IO.Path.GetFullPath(root);
            string parent = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())) + System.IO.Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                || !System.IO.Path.GetFileName(resolved).StartsWith("HealthMonitor-admission-", StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path validation failed.");
            Directory.Delete(resolved, recursive: true);
        }
        /// <summary>Checks persisted state independently of provider call counts and returned decisions.</summary>
        internal int Count() { using var document = JsonDocument.Parse(File.ReadAllText(Path)); return document.RootElement.GetProperty("count").GetInt32(); }
    }
}
