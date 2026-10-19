using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;

namespace HealthMonitor.Api.AiQueries;

/// <summary>Owns the named AI admission policy using framework fixed windows, client partitions and nonqueued concurrency.</summary>
/// <remarks>This unregistered primitive performs no HTTP, provider, daily-budget or configuration work. The future caller
/// must validate the prompt first, supply only trusted connection identity, then persist a daily reservation after admission.
/// Global-first acquisition bounds creation of client partitions during globally rejected address churn. It is conservative,
/// not fair: repeated over-limit requests from one client can consume all global tokens and deny other clients.
/// Fixed-window tokens are never refunded when a later gate fails. Framework partitions own idle cleanup; no custom
/// address dictionary is maintained. Owners must drain acquired leases before disposing the policy.</remarks>
internal sealed class AiAdmissionPolicy : IDisposable
{
    internal const string Name = "AiTranslation";
    internal const int ClientLimit = 5;
    internal const int GlobalLimit = 10;
    internal const int ConcurrencyLimit = 2;
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Separates acquired capacity, ordinary rate/concurrency rejection, and unavailable trusted identity/lifecycle.</summary>
    internal enum Outcome { Acquired, RateLimited, Unavailable }

    private readonly FixedWindowRateLimiter global;
    private readonly PartitionedRateLimiter<IPAddress> clients;
    private readonly ConcurrencyLimiter concurrency;
    private int disposed;

    /// <summary>Constructs the exact production policy; no runtime override weakens its limits.</summary>
    internal AiAdmissionPolicy() : this(Window) { }

    /// <summary>Allows a shorter framework window only for bounded replenishment tests, not through runtime configuration.</summary>
    /// <param name="window">Positive window no longer than the production minute.</param>
    /// <exception cref="ArgumentOutOfRangeException">The controlled test window is outside the permitted range.</exception>
    internal AiAdmissionPolicy(TimeSpan window)
    {
        if (window <= TimeSpan.Zero || window > Window) throw new ArgumentOutOfRangeException(nameof(window));
        global = new(new() { PermitLimit = GlobalLimit, Window = window, QueueLimit = 0, AutoReplenishment = true });
        clients = PartitionedRateLimiter.Create<IPAddress, IPAddress>(address =>
            RateLimitPartition.GetFixedWindowLimiter(address, _ => new FixedWindowRateLimiterOptions
            { PermitLimit = ClientLimit, Window = window, QueueLimit = 0, AutoReplenishment = true }));
        concurrency = new(new() { PermitLimit = ConcurrencyLimit, QueueLimit = 0 });
    }

    /// <summary>Attempts all three gates synchronously without queuing; partial acquisitions are released on every exit.</summary>
    /// <param name="client">Actual trusted remote address, never a forwarded-header string supplied directly by a caller.</param>
    /// <param name="token">Cancellation checked before acquisition and before publishing a successful lease.</param>
    /// <returns>An owned lease; successful concurrency must be held until downstream provider work has actually drained.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels before the successful lease is published.</exception>
    internal Lease TryEnter(IPAddress? client, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref disposed) != 0 || Normalize(client) is not { } address)
            return new(Outcome.Unavailable, null, null);
        var held = new List<RateLimitLease>(3);
        try
        {
            var lease = global.AttemptAcquire(); held.Add(lease);
            if (!lease.IsAcquired) return Rejected(lease);
            lease = clients.AttemptAcquire(address); held.Add(lease);
            if (!lease.IsAcquired) return Rejected(lease);
            lease = concurrency.AttemptAcquire(); held.Add(lease);
            if (!lease.IsAcquired) return Rejected(lease);
            token.ThrowIfCancellationRequested();
            var admitted = new Lease(Outcome.Acquired, null, held.ToArray());
            held.Clear(); // Ownership transfers only after cancellation checks; disposal cannot refund window tokens.
            return admitted;
        }
        finally { for (int index = held.Count - 1; index >= 0; index--) held[index].Dispose(); }
    }

    /// <summary>Returns only positive rounded retry metadata; no client identity or framework diagnostic is exposed.</summary>
    private static Lease Rejected(RateLimitLease lease)
    {
        double seconds = lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retry) ? Math.Ceiling(retry.TotalSeconds) : 1;
        return new(Outcome.RateLimited, TimeSpan.FromSeconds(Math.Max(1, seconds)), null);
    }

    /// <summary>Copies the trusted key, preserves IPv6 scope, and unifies mapped IPv4 so aliases cannot double a client quota.</summary>
    private static IPAddress? Normalize(IPAddress? client)
    {
        if (client is null) return null;
        if (client.IsIPv4MappedToIPv6) client = client.MapToIPv4();
        if (client.Equals(IPAddress.Any) || client.Equals(IPAddress.IPv6Any) || client.Equals(IPAddress.None)) return null;
        return client.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPAddress(client.GetAddressBytes(), client.ScopeId) : new IPAddress(client.GetAddressBytes());
    }

    /// <summary>Owns acquired permits exactly once; rejected leases hold no limiter resource.</summary>
    internal sealed class Lease : IDisposable
    {
        private RateLimitLease[]? held;
        internal Outcome Status { get; }
        internal TimeSpan? RetryAfter { get; }

        /// <summary>Retains only owned status/retry metadata and successful framework leases; never an address or prompt.</summary>
        internal Lease(Outcome status, TimeSpan? retryAfter, RateLimitLease[]? held)
        { Status = status; RetryAfter = retryAfter; this.held = held; }

        /// <summary>Releases concurrency once, including concurrent repeated disposal; window tokens remain consumed.</summary>
        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref held, null);
            if (owned is null) return;
            for (int index = owned.Length - 1; index >= 0; index--) owned[index].Dispose();
        }
    }

    /// <summary>Disposes owned timers/partitions after callers have drained; repeated disposal is harmless.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { clients.Dispose(); }
        finally { try { global.Dispose(); } finally { concurrency.Dispose(); } }
    }
}
