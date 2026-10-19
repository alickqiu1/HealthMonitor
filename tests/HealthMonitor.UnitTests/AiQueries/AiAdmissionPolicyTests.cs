using System.Diagnostics;
using System.Net;
using HealthMonitor.Api.AiQueries;
using Xunit;
using static HealthMonitor.Api.AiQueries.AiAdmissionPolicy;

namespace HealthMonitor.UnitTests.AiQueries;

/// <summary>Verifies owned policy accounting and leases using framework primitives only; all addresses are in-memory keys.</summary>
public sealed class AiAdmissionPolicyTests
{
    [Fact]
    public void DefaultPolicy_ExactNameMinuteAndLimits()
    {
        Assert.Equal("AiTranslation", Name); Assert.Equal(TimeSpan.FromMinutes(1), Window);
        Assert.Equal(5, ClientLimit); Assert.Equal(10, GlobalLimit); Assert.Equal(2, ConcurrencyLimit);
    }

    [Fact]
    public void ClientFiveThenSixthRejected_DenialStillConsumesGlobalToken()
    {
        using var policy = new AiAdmissionPolicy();
        for (int index = 0; index < 5; index++) Acquired(policy, Address(1));
        using (var sixth = policy.TryEnter(Address(1))) Rejected(sixth);
        for (int index = 0; index < 4; index++) Acquired(policy, Address(2));
        using var eleventh = policy.TryEnter(Address(3)); Rejected(eleventh);
        // The client's rejected sixth request consumed the global sixth token: no refund and no fairness guarantee.
    }

    [Fact]
    public void GlobalTenAcrossClients_ThenEleventhRejected()
    {
        using var policy = new AiAdmissionPolicy();
        for (int index = 1; index <= 10; index++) Acquired(policy, Address(index));
        using var rejected = policy.TryEnter(Address(11)); Rejected(rejected);
    }

    [Fact]
    public void ConcurrentTwoThenThirdRejected_OnceDisposalRestoresOnlyOnePermit()
    {
        using var policy = new AiAdmissionPolicy();
        using var first = policy.TryEnter(Address(1)); using var second = policy.TryEnter(Address(2));
        Assert.Equal(Outcome.Acquired, first.Status); Assert.Equal(Outcome.Acquired, second.Status);
        using (var third = policy.TryEnter(Address(3)))
        { Rejected(third); Assert.Equal(TimeSpan.FromSeconds(1), third.RetryAfter); }
        Parallel.Invoke(first.Dispose, first.Dispose, first.Dispose);
        using var replacement = policy.TryEnter(Address(4)); Assert.Equal(Outcome.Acquired, replacement.Status);
        using var stillFull = policy.TryEnter(Address(5)); Rejected(stillFull);
    }

    [Fact]
    public async Task ParallelCallers_AtMostTwoHeldAndNoWaitingQueue()
    {
        using var policy = new AiAdmissionPolicy();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempted = 0, acquired = 0;
        var workers = Enumerable.Range(1, 16).Select(index => Task.Run(async () =>
        {
            using var lease = policy.TryEnter(Address(index));
            if (lease.Status == Outcome.Acquired) Interlocked.Increment(ref acquired);
            if (Interlocked.Increment(ref attempted) == 16) allAttempted.TrySetResult();
            if (lease.Status == Outcome.Acquired) await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return lease.Status == Outcome.Acquired;
        })).ToArray();
        try
        {
            await allAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, Volatile.Read(ref acquired));
            Assert.Equal(16, Volatile.Read(ref attempted));
        }
        finally { release.TrySetResult(); await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(15)); }
        Assert.Equal(2, workers.Count(worker => worker.Result));
        Assert.Equal(14, workers.Count(worker => !worker.Result));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("255.255.255.255")]
    [InlineData("::ffff:0.0.0.0")]
    public void InvalidIdentity_FailsClosedWithoutConsumingGlobalTokens(string? text)
    {
        using var policy = new AiAdmissionPolicy();
        using (var denied = policy.TryEnter(text is null ? null : IPAddress.Parse(text)))
        { Assert.Equal(Outcome.Unavailable, denied.Status); Assert.Null(denied.RetryAfter); }
        for (int index = 1; index <= 10; index++) Acquired(policy, Address(index));
    }

    [Fact]
    public void MappedIpv4_SharesOriginalClientQuota()
    {
        using var policy = new AiAdmissionPolicy();
        var native = Address(1); var mapped = native.MapToIPv6();
        for (int index = 0; index < 5; index++) Acquired(policy, index % 2 == 0 ? native : mapped);
        using var sixth = policy.TryEnter(native); Rejected(sixth);
    }

    [Fact]
    public void Ipv6Scope_IsPreservedAndMutableCallerKeyIsCopied()
    {
        using var policy = new AiAdmissionPolicy();
        var mutable = IPAddress.Parse("fe80::1%3"); Acquired(policy, mutable);
        mutable.ScopeId = 4;
        for (int index = 0; index < 4; index++) Acquired(policy, IPAddress.Parse("fe80::1%3"));
        using (var sixth = policy.TryEnter(IPAddress.Parse("fe80::1%3"))) Rejected(sixth);
        Acquired(policy, mutable);
    }

    [Fact]
    public void PreCancelledCall_DoesNotConsumeAndDoesNotCancelOtherLease()
    {
        using var policy = new AiAdmissionPolicy();
        using var active = policy.TryEnter(Address(1));
        using var abort = new CancellationTokenSource(); abort.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => policy.TryEnter(Address(2), abort.Token));
        using var other = policy.TryEnter(Address(2)); Assert.Equal(Outcome.Acquired, other.Status);
        using var third = policy.TryEnter(Address(3)); Rejected(third);
        Assert.Equal(Outcome.Acquired, active.Status);
    }

    [Fact]
    public void DisposedPolicy_FailsClosedAndRepeatedDisposalIsHarmless()
    {
        var policy = new AiAdmissionPolicy(); policy.Dispose(); policy.Dispose();
        using var result = policy.TryEnter(Address(1));
        Assert.Equal(Outcome.Unavailable, result.Status); Assert.Null(result.RetryAfter);
    }

    [Fact]
    public async Task ControlledShortWindow_FrameworkEventuallyReplenishesBothRateGates()
    {
        using var policy = new AiAdmissionPolicy(TimeSpan.FromMilliseconds(500));
        for (int index = 0; index < 5; index++) Acquired(policy, Address(1));
        for (int index = 0; index < 5; index++) Acquired(policy, Address(2));
        using (var denied = policy.TryEnter(Address(1))) Rejected(denied);
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(50);
            using var lease = policy.TryEnter(Address(1));
            if (lease.Status == Outcome.Acquired) return;
            Rejected(lease);
        }
        Assert.Fail("Framework rate gates did not replenish within the test deadline.");
        // This bounded real-timer check is not proof of the exact production-minute temporal boundary.
    }

    [Fact]
    public void TestWindow_RejectsNonpositiveOrLongerThanProduction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiAdmissionPolicy(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiAdmissionPolicy(TimeSpan.FromMinutes(2)));
    }

    /// <summary>Creates documentation-reserved address keys without DNS, sockets or forwarding headers.</summary>
    private static IPAddress Address(int index) => IPAddress.Parse("192.0.2." + index);

    /// <summary>Requires successful capacity and disposes its concurrency lease immediately after this sequential request.</summary>
    private static void Acquired(AiAdmissionPolicy policy, IPAddress address)
    { using var lease = policy.TryEnter(address); Assert.Equal(Outcome.Acquired, lease.Status); Assert.Null(lease.RetryAfter); }

    /// <summary>Requires safe, positive, whole-second Retry-After metadata for owned rate/concurrency failures.</summary>
    private static void Rejected(Lease lease)
    {
        Assert.Equal(Outcome.RateLimited, lease.Status);
        Assert.NotNull(lease.RetryAfter); Assert.True(lease.RetryAfter.Value >= TimeSpan.FromSeconds(1));
        Assert.Equal(Math.Ceiling(lease.RetryAfter.Value.TotalSeconds), lease.RetryAfter.Value.TotalSeconds);
    }
}
