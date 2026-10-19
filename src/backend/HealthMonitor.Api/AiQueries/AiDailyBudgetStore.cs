using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HealthMonitor.Api.AiQueries;

/// <summary>Reserves at most one hundred admitted attempts per UTC day in a private, operator-provisioned counter.</summary>
/// <remarks>This unregistered single-owner operational store contains no prompts or application data. The host must
/// provide a trusted path outside the web root and provision its initial counter separately; missing state never means zero.
/// A stable sibling lock protects cooperating processes across atomic counter replacement. Filesystem permissions and
/// backup/restore are trusted: a fresh process cannot detect an offline rollback to another valid counter.
/// Operators must stop the owner before repair/restore; unlocked concurrent administrative writes are unsupported.
/// Flush and same-directory replacement request filesystem persistence, not a universal power-loss durability guarantee.
/// Owners must drain calls before disposal. Filesystem cancellation is cooperative, not a hard completion deadline.</remarks>
internal sealed class AiDailyBudgetStore : IAsyncDisposable
{
    /// <summary>Separates an admitted reservation, ordinary daily exhaustion, and fail-closed storage unavailability.</summary>
    internal enum Outcome { Admitted, Exhausted, Unavailable }

    private readonly string path;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private FileStream? owner;
    private State? floor;
    private bool unavailable;
    private bool disposed;

    /// <summary>Retains an explicit counter path without creating directories or counter state.</summary>
    /// <param name="path">Fully qualified trusted counter path; deployment must also enforce location and permissions.</param>
    /// <param name="clock">UTC admission clock, controlled in tests.</param>
    /// <exception cref="ArgumentException">The path is absent or not fully qualified; its value is never echoed.</exception>
    /// <exception cref="ArgumentNullException">The clock is null.</exception>
    internal AiDailyBudgetStore(string path, TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("A fully qualified private counter path is required.", nameof(path));
        ArgumentNullException.ThrowIfNull(clock);
        this.path = path;
        this.clock = clock;
    }

    /// <summary>Atomically persists one reservation before granting admission; never refunds a committed attempt.</summary>
    /// <param name="token">Cancellation before replacement may abandon this reservation without changing the counter.</param>
    /// <returns>Admitted only after replacement succeeds; faults latch Unavailable until repair and a new store lifetime.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed before the commit boundary.</exception>
    /// <exception cref="ObjectDisposedException">The owner has disposed the store.</exception>
    /// <remarks>Cancellation after successful replacement does not reverse its count. Caller/provider failure likewise cannot
    /// refund it. No provider is called here. A changed file is revalidated on every attempt and may not regress below
    /// this owner's paired day/count floor; newer valid counts are accepted conservatively.</remarks>
    internal async Task<Outcome> TryReserveAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (unavailable) return Outcome.Unavailable;
            try
            {
                // Lock the stable sibling, not the counter inode that atomic replacement changes. Never unlink this lock.
                owner ??= new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var state = await ReadAsync(token);
                var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
                if (state.Day > today || (floor is { } prior &&
                    (state.Day < prior.Day || (state.Day == prior.Day && state.Count < prior.Count))))
                    return Disable();
                floor = state;
                if (state.Day == today && state.Count == 100) return Outcome.Exhausted;
                // A valid old day resets only as part of a new persisted reservation, never on a failed read or restart.
                var next = new State(today, state.Day == today ? state.Count + 1 : 1);
                await PersistAsync(next, token);
                floor = next;
                return Outcome.Admitted;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException)
            {
                // Uncertain writes and unreadable state cannot silently restore budget; do not retain paths or raw diagnostics.
                return Disable();
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>Reads only a bounded strict UTF-8 document with exactly the owned version/day/count contract.</summary>
    private async Task<State> ReadAsync(CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        byte[] bytes = new byte[129];
        int length = 0;
        while (length < bytes.Length)
        {
            int read = await stream.ReadAsync(bytes.AsMemory(length), token);
            if (read == 0) break;
            length += read;
        }
        if (length > 128) throw new JsonException();
        string json = new UTF8Encoding(false, true).GetString(bytes, 0, length);
        return ParseState(json);
    }

    /// <summary>Normalizes malformed JSON string/name decoding locally; programming faults elsewhere are not reclassified.</summary>
    private static State ParseState(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name) || property.Name is not ("version" or "day" or "count")) throw new JsonException();
            if (names.Count != 3
                || root.GetProperty("version").ValueKind != JsonValueKind.Number
                || !root.GetProperty("version").TryGetInt32(out int version) || version != 1
                || root.GetProperty("day").ValueKind != JsonValueKind.String
                || !DateOnly.TryParseExact(root.GetProperty("day").GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var day)
                || root.GetProperty("count").ValueKind != JsonValueKind.Number
                || !root.GetProperty("count").TryGetInt32(out int count) || count is < 0 or > 100)
                throw new JsonException();
            return new(day, count);
        }
        catch (InvalidOperationException) { throw new JsonException(); }
    }

    /// <summary>Writes an exclusively created sibling, flushes it, then replaces the counter in the same directory.</summary>
    /// <remarks>Only this invocation's created pending file is eligible for cleanup. A pre-existing pending file is preserved
    /// and fails closed. The final cancellation check is before replacement: successful replacement is irrevocable here.</remarks>
    private async Task PersistAsync(State next, CancellationToken token)
    {
        string pending = path + ".pending";
        bool owned = false;
        try
        {
            await using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                owned = true;
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
                { version = 1, day = next.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), count = next.Count });
                await stream.WriteAsync(bytes, token);
                await stream.FlushAsync(token);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(pending, path, overwrite: true);
            owned = false;
        }
        finally
        {
            if (owned)
            {
                try { File.Delete(pending); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { Disable(); } // Cleanup cannot mask cancellation/primary failure or make an uncertain store reusable.
            }
        }
    }

    /// <summary>Latches storage failure; repairing the file alone cannot resume this running owner.</summary>
    private Outcome Disable() { unavailable = true; return Outcome.Unavailable; }

    /// <summary>Retains the monotonic pair so old-day replacement cannot evade a same-day count floor.</summary>
    private readonly record struct State(DateOnly Day, int Count);

    /// <summary>Releases the lifetime lock after callers have drained; never deletes the stable lock file or counter.</summary>
    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            owner?.Dispose();
            gate.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
