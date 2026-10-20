using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using HealthMonitor.Api.AiQueries;
using HealthMonitor.Application.AiQueries;
using HealthMonitor.Application.AiQueries.Ports;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace HealthMonitor.UnitTests.AiQueries;

/// <summary>Exercises whole-operation ownership with real admission/counter resources and actual pending fake-provider tasks.</summary>
public sealed class AiQueryRuntimeTests
{
    private const string Canary = "private-runtime-provider-canary";

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task NullDependency_DoesNotAdoptAnyResources(int missing)
    {
        await using var setup = new Setup();
        Assert.Throws<ArgumentNullException>(() => new AiQueryRuntime(missing == 0 ? null! : setup.Provider,
            missing == 1 ? null! : setup.Policy, missing == 2 ? null! : setup.Budget,
            missing == 3 ? null! : setup.Clock, missing == 4 ? null! : setup.Log));
        Assert.Equal(0, setup.Provider.Disposals);
        await setup.Call(); Assert.Equal(1, setup.Count());
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(5001)]
    public async Task InvalidWait_DoesNotAdoptResources(int milliseconds)
    {
        await using var setup = new Setup();
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiQueryRuntime(setup.Provider, setup.Policy, setup.Budget,
            setup.Clock, setup.Log, TimeSpan.FromMilliseconds(milliseconds)));
        await setup.Call(); Assert.Equal(0, setup.Provider.Disposals); Assert.Equal(1, setup.Count());
    }

    [Fact]
    public async Task NonDisposableProviderAndNullContext_AreRejected()
    {
        await using var setup = new Setup();
        Assert.Throws<ArgumentException>(() => new AiQueryRuntime(new NonDisposableProvider(), setup.Policy, setup.Budget, setup.Clock, setup.Log));
        Assert.Throws<ArgumentNullException>(() => setup.Runtime.CreateGenerator(null!));
        await setup.Call(); Assert.Equal(1, setup.Count());
    }

    [Fact]
    public async Task SuccessfulOperation_ForwardsExactArgumentsAndUntrustedProposal()
    {
        await using var setup = new Setup(TimeSpan.FromSeconds(5));
        using var caller = new CancellationTokenSource();
        var result = await setup.Call(caller.Token);
        Assert.Same(setup.Provider.Proposal, result);
        var arguments = Assert.Single(setup.Provider.Arguments);
        Assert.Equal("devices", arguments.Prompt); Assert.Same(setup.Vocabulary, arguments.Vocabulary); Assert.Equal(caller.Token, arguments.Token);
        Assert.Equal(1, setup.Count()); Assert.Equal(0, setup.Provider.Disposals);
        await setup.Runtime.DisposeAsync(); await setup.Runtime.CleanupCompletion;
        Assert.Equal(1, setup.Provider.Disposals); await setup.AssertResourcesReleasedAsync();
    }

    [Fact]
    public async Task ProviderFailure_PropagatesUnchangedWithoutRetry()
    {
        await using var setup = new Setup();
        setup.Provider.Failure = new InvalidOperationException(Canary);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Call());
        Assert.Same(setup.Provider.Failure, error); Assert.Single(setup.Provider.Arguments); Assert.Equal(1, setup.Count());
        Assert.DoesNotContain(Canary, string.Join(" ", setup.Log.Messages));
    }

    [Fact]
    public async Task MissingIdentity_LeavesBudgetsAndProviderUntouched()
    {
        await using var setup = new Setup();
        var error = await Assert.ThrowsAsync<AiAdmissionRejectedException>(() => setup.Call(verified: false));
        Assert.Equal(AiAdmissionRejectedException.Rejection.AdmissionUnavailable, error.Reason);
        Assert.Empty(setup.Provider.Arguments); Assert.Equal(0, setup.Count()); Assert.False(File.Exists(setup.Path + ".lock"));
    }

    [Fact]
    public async Task NormalShutdown_DrainsWholeOperationBeforeResourceDisposal()
    {
        await using var setup = new Setup(TimeSpan.FromSeconds(5));
        setup.Provider.Hold = true;
        var call = setup.Call(); await setup.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = setup.Runtime.DisposeAsync().AsTask();
        Assert.False(setup.Runtime.CleanupCompletion.IsCompleted); Assert.Equal(0, setup.Provider.Disposals); setup.AssertLockRetained();
        setup.Provider.Release.TrySetResult();
        await call; await stop; await setup.Runtime.CleanupCompletion;
        Assert.False(setup.Provider.DisposedWhileActive); Assert.Equal(1, setup.Provider.Disposals); await setup.AssertResourcesReleasedAsync();
    }

    [Fact]
    public async Task Stopping_RejectsNewWorkAndPreservesCallerCancellation()
    {
        await using var setup = new Setup();
        setup.Provider.Hold = true;
        var call = setup.Call(); await setup.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await setup.Runtime.DisposeAsync();
        var error = await Assert.ThrowsAsync<AiAdmissionRejectedException>(() => setup.Call());
        Assert.Equal(AiAdmissionRejectedException.Rejection.AdmissionUnavailable, error.Reason);
        using var caller = new CancellationTokenSource(); caller.Cancel();
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.Call(caller.Token));
        Assert.Equal(caller.Token, cancelled.CancellationToken);
        Assert.Equal(1, setup.Count()); Assert.Single(setup.Provider.Arguments); setup.AssertLockRetained();
        setup.Provider.Release.TrySetResult(); await call; await setup.Runtime.CleanupCompletion;
    }

    [Fact]
    public async Task UncooperativeCancelledCall_RetainsOwnershipUntilActualTermination()
    {
        await using var setup = new Setup(); using var caller = new CancellationTokenSource();
        setup.Provider.Hold = true;
        var call = setup.Call(caller.Token); await setup.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel(); await setup.Runtime.DisposeAsync();
        Assert.False(call.IsCompleted); Assert.False(setup.Runtime.CleanupCompletion.IsCompleted);
        Assert.Equal(0, setup.Provider.Disposals); setup.AssertLockRetained();
        setup.Provider.Release.TrySetResult();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(caller.Token, error.CancellationToken);
        await setup.Runtime.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(5)); await setup.AssertResourcesReleasedAsync();
    }

    [Fact]
    public async Task LateProviderFault_RemainsObservedAfterBoundedShutdown()
    {
        await using var setup = new Setup(); setup.Provider.Hold = true; setup.Provider.Failure = new InvalidOperationException(Canary);
        var call = setup.Call(); await setup.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await setup.Runtime.DisposeAsync(); setup.AssertLockRetained();
        setup.Provider.Release.TrySetResult();
        Assert.Same(setup.Provider.Failure, await Assert.ThrowsAsync<InvalidOperationException>(() => call));
        await setup.Runtime.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, setup.Provider.Disposals); Assert.False(setup.Provider.DisposedWhileActive);
        Assert.DoesNotContain(Canary, string.Join(" ", setup.Log.Messages)); await setup.AssertResourcesReleasedAsync();
    }

    [Fact]
    public async Task ConcurrentDisposal_SharesOneTaskAndCleansOnce()
    {
        await using var setup = new Setup(); setup.Provider.Hold = true;
        var call = setup.Call(); await setup.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var sharedTasks = new ConcurrentBag<Task>();
        var stops = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        { var shared = setup.Runtime.DisposeAsync().AsTask(); sharedTasks.Add(shared); await shared; })).ToArray();
        await Task.WhenAll(stops).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(8, sharedTasks.Count);
        Assert.All(sharedTasks, shared => Assert.Same(setup.Runtime.DisposeAsync().AsTask(), shared));
        Assert.Equal(0, setup.Provider.Disposals); setup.AssertLockRetained();
        setup.Provider.Release.TrySetResult(); await call; await setup.Runtime.CleanupCompletion;
        await setup.Runtime.DisposeAsync(); Assert.Equal(1, setup.Provider.Disposals); await setup.AssertResourcesReleasedAsync();
    }

    [Fact]
    public async Task ConcurrentEntryAndStop_LinearizesBeforeAnyResourceUse()
    {
        for (int iteration = 0; iteration < 8; iteration++)
        {
            await using var setup = new Setup();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var call = Task.Run(async () => { await start.Task; return await Record.ExceptionAsync(() => setup.Call()); });
            var stop = Task.Run(async () => { await start.Task; await setup.Runtime.DisposeAsync(); });
            start.SetResult();
            var failure = await call.WaitAsync(TimeSpan.FromSeconds(5)); await stop.WaitAsync(TimeSpan.FromSeconds(5));
            await setup.Runtime.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            if (failure is not null) Assert.Equal(AiAdmissionRejectedException.Rejection.AdmissionUnavailable, Assert.IsType<AiAdmissionRejectedException>(failure).Reason);
            Assert.Equal(failure is null ? 1 : 0, setup.Count()); Assert.Equal(setup.Count(), setup.Provider.Arguments.Count);
            Assert.Equal(1, setup.Provider.Disposals); await setup.AssertResourcesReleasedAsync();
        }
    }

    [Fact]
    public async Task SynchronousProviderDisposal_CannotBlockBoundedHostWait()
    {
        await using var setup = new Setup(); await setup.Call(); setup.Provider.HoldDisposal = true;
        var stop = setup.Runtime.DisposeAsync().AsTask();
        await setup.Provider.DisposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(setup.Runtime.CleanupCompletion.IsCompleted); setup.AssertLockRetained();
        setup.Provider.ReleaseDisposal.TrySetResult();
        await setup.Runtime.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(5)); await setup.AssertResourcesReleasedAsync();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CleanupFailure_AttemptsAllResourcesAndUsesSafeDiagnostics(bool loggerThrows)
    {
        await using var setup = new Setup(TimeSpan.FromSeconds(5)); await setup.Call();
        setup.Provider.ThrowOnDisposal = true; setup.Log.Throw = loggerThrows;
        var stop = setup.Runtime.DisposeAsync().AsTask();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => stop);
        Assert.Equal("AI resource cleanup failed.", error.Message); Assert.Null(error.InnerException);
        Assert.Same(stop, setup.Runtime.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Runtime.CleanupCompletion);
        Assert.Equal(1, setup.Provider.Disposals); await setup.AssertResourcesReleasedAsync();
        Assert.DoesNotContain(Canary, string.Join(" ", setup.Log.Messages)); Assert.All(setup.Log.Exceptions, exception => Assert.Null(exception));
    }

    /// <summary>Uses a stable UTC day shared with the real counter; it is borrowed and has no disposal ownership.</summary>
    private sealed class Clock : TimeProvider
    {
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    }

    /// <summary>Provides real controllable async work and synchronous cleanup, deliberately ignoring cancellation when held.</summary>
    private sealed class Provider : IStructuredQueryGenerator, IDisposable
    {
        internal bool Hold, HoldDisposal, ThrowOnDisposal;
        internal Exception? Failure;
        internal StructuredQueryProposal Proposal { get; } = new("UnvalidatedProposal", null, null);
        internal ConcurrentQueue<(string Prompt, QueryVocabulary Vocabulary, CancellationToken Token)> Arguments { get; } = new();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposalEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int active, disposals;
        internal int Disposals => Volatile.Read(ref disposals);
        internal bool DisposedWhileActive { get; private set; }
        /// <inheritdoc />
        public async Task<StructuredQueryProposal?> GenerateAsync(string prompt, QueryVocabulary vocabulary, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref active); Arguments.Enqueue((prompt, vocabulary, cancellationToken)); Entered.TrySetResult();
            try { if (Hold) await Release.Task; if (Failure is not null) throw Failure; return Proposal; }
            finally { Interlocked.Decrement(ref active); }
        }
        /// <summary>Can block or fail to verify ownership safety beyond the ordinary provider-call lifetime.</summary>
        public void Dispose()
        {
            Interlocked.Increment(ref disposals); DisposedWhileActive = Volatile.Read(ref active) != 0; DisposalEntered.TrySetResult();
            if (HoldDisposal) ReleaseDisposal.Task.GetAwaiter().GetResult();
            if (ThrowOnDisposal) throw new InvalidOperationException(Canary);
        }
    }

    /// <summary>Exercises the constructor's explicit disposal-ownership precondition without inventing a production interface.</summary>
    private sealed class NonDisposableProvider : IStructuredQueryGenerator
    {
        /// <inheritdoc />
        public Task<StructuredQueryProposal?> GenerateAsync(string prompt, QueryVocabulary vocabulary, CancellationToken cancellationToken) => Task.FromResult<StructuredQueryProposal?>(null);
    }

    /// <summary>Captures formatted diagnostics and attached exceptions; a failing sink tests cleanup independence.</summary>
    private sealed class Logs : ILogger<AiQueryRuntime>
    {
        internal bool Throw;
        internal ConcurrentQueue<string> Messages { get; } = new();
        internal ConcurrentQueue<Exception?> Exceptions { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { Messages.Enqueue(formatter(state, exception)); Exceptions.Enqueue(exception); if (Throw) throw new InvalidOperationException(Canary); }
    }

    /// <summary>Transfers real resources to the runtime, then always releases fake barriers and awaits actual cleanup before deleting known files.</summary>
    private sealed class Setup : IAsyncDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HealthMonitor-runtime-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(root, "counter.json");
        internal Clock Clock { get; } = new();
        internal Provider Provider { get; } = new();
        internal Logs Log { get; } = new();
        internal AiAdmissionPolicy Policy { get; } = new();
        internal AiDailyBudgetStore Budget { get; }
        internal AiQueryRuntime Runtime { get; }
        internal QueryVocabulary Vocabulary { get; } = new(["DEN"], ["Server"]);
        /// <summary>Seeds only this unique fixture's counter, using a short internal shutdown wait for deferred-cleanup tests.</summary>
        internal Setup(TimeSpan? wait = null)
        {
            Directory.CreateDirectory(root); File.WriteAllText(Path, "{\"version\":1,\"day\":\"2026-10-19\",\"count\":0}");
            Budget = new(Path, Clock); Runtime = new(Provider, Policy, Budget, Clock, Log, wait ?? TimeSpan.FromMilliseconds(50));
        }
        /// <summary>Provides explicit synthetic ingress metadata; no actual transport or production routing is claimed here.</summary>
        internal Task<StructuredQueryProposal?> Call(CancellationToken token = default, bool verified = true)
        {
            var context = new DefaultHttpContext();
            if (verified) context.Features.Set(AiClientIdentity.Capture(IPAddress.Parse("192.0.2.1")));
            return Runtime.CreateGenerator(context).GenerateAsync("devices", Vocabulary, token);
        }
        /// <summary>Checks the persisted charge independently of return values.</summary>
        internal int Count() { using var document = JsonDocument.Parse(File.ReadAllText(Path)); return document.RootElement.GetProperty("count").GetInt32(); }
        /// <summary>Requires the real lifetime file lock to remain exclusive while work or cleanup is unresolved.</summary>
        internal void AssertLockRetained() => Assert.Throws<IOException>(() => { using var competitor = File.Open(Path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
        /// <summary>Verifies the disposed policy fails closed, the counter rejects use and its lifetime lock is released.</summary>
        internal async Task AssertResourcesReleasedAsync()
        {
            using var rejected = Policy.TryEnter(IPAddress.Loopback);
            Assert.Equal(AiAdmissionPolicy.Outcome.Unavailable, rejected.Status);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => Budget.TryReserveAsync());
            if (File.Exists(Path + ".lock")) { using var available = File.Open(Path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        }
        /// <summary>Never deletes the counter beneath unresolved operations, including after an assertion fails.</summary>
        public async ValueTask DisposeAsync()
        {
            Provider.Release.TrySetResult(); Provider.ReleaseDisposal.TrySetResult();
            try { await Runtime.DisposeAsync(); } catch (InvalidOperationException) when (Provider.ThrowOnDisposal) { }
            var completion = Runtime.CleanupCompletion;
            await DrainCleanupAsync(completion);
            try { await completion; } catch (InvalidOperationException) when (Provider.ThrowOnDisposal) { }
            string resolved = System.IO.Path.GetFullPath(root);
            string parent = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())) + System.IO.Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(resolved).StartsWith("HealthMonitor-runtime-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe runtime cleanup path.");
            File.Delete(Path); File.Delete(Path + ".lock"); Directory.Delete(root);
        }
        /// <summary>A timeout cannot authorize counter deletion; fail-stop if owned cleanup cannot reach a terminal state.</summary>
        private static async Task DrainCleanupAsync(Task completion)
        {
            using var timer = new CancellationTokenSource();
            if (await Task.WhenAny(completion, Task.Delay(TimeSpan.FromSeconds(10), timer.Token)) != completion)
            { Console.Error.WriteLine("Runtime test cleanup did not terminate; stopping without unsafe file deletion."); Environment.Exit(1); }
            timer.Cancel();
        }
    }
}
