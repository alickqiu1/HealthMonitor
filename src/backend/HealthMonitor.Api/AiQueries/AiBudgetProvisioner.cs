using System.Globalization;
using System.Text.Json;

namespace HealthMonitor.Api.AiQueries;

/// <summary>Creates the single-replica AI counter once, through an explicit operator command rather than normal startup.</summary>
/// <remarks>The operator must presecure a persisted private parent directory outside the actual served web root.
/// Attribute checks reject links but do not establish ownership/ACLs or eliminate path-swap races. Trusted directory
/// control remains a prerequisite. Existing counters are never read, adopted, truncated, reset, repaired or deleted.</remarks>
internal static class AiBudgetProvisioner
{
    /// <summary>Locks the runtime sidecar and creates one flushed zero-count UTC-day state without enabling AI.</summary>
    /// <param name="configuration">Only AiQueries:BudgetCounterPath is inspected, never provider, key or connection-string values.</param>
    /// <param name="environment">Actual host paths for lexical web-root exclusion.</param>
    /// <param name="clock">Borrowed clock sampled once immediately before creation.</param>
    /// <param name="cancellationToken">Caller cancellation; a possibly written file is retained for explicit inspection.</param>
    /// <returns>Completion after writing/flushing and releasing only this operation's handles.</returns>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels; uncertain state is never deleted or retried.</exception>
    /// <exception cref="InvalidOperationException">The configured path, existing state, permissions or I/O prevent safe creation.</exception>
    /// <remarks>Pre-cancelled entry creates no artifacts. Cancellation/failure after acquiring the stable sidecar can
    /// leave that file and a partial or complete new counter. Inspect before retrying; this operation never resets it.
    /// Normal API startup still fails closed on missing/corrupt state and cannot repair it automatically.</remarks>
    internal static async Task CreateAsync(IConfiguration configuration, IWebHostEnvironment environment, TimeProvider clock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration); ArgumentNullException.ThrowIfNull(environment); ArgumentNullException.ThrowIfNull(clock);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string path = AiQueryOptions.ReadBudgetCounterPath(configuration, environment);
            CheckParents(path);
            string lockPath = path + ".lock";
            // OpenOrCreate must not follow a pre-existing sidecar link into an unrelated file.
            try
            {
                var attributes = File.GetAttributes(lockPath);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw Failure();
            }
            catch (FileNotFoundException) { /* A genuinely absent sidecar is created under the trusted parent. */ }
            cancellationToken.ThrowIfCancellationRequested();
            await using var ownership = Open(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite);
            string day = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            cancellationToken.ThrowIfCancellationRequested();
            // CreateNew atomically refuses every existing leaf, including corrupt/partial files and symbolic links.
            await using var counter = Open(path, FileMode.CreateNew, FileAccess.Write);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, day, count = 0 });
            await counter.WriteAsync(bytes, cancellationToken);
            await counter.FlushAsync(cancellationToken);
            // Flush file contents; the parent directory is not fsynced, so no power-loss durability is promised.
            counter.Flush(flushToDisk: true);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        { cancellationToken.ThrowIfCancellationRequested(); throw; }
        catch
        {
            // CLI errors may be copied publicly. Retain uncertain state and emit no path or original exception payload.
            throw Failure();
        }
    }

    /// <summary>Requires every existing ancestor to be a real directory; it never creates directories or changes ACLs.</summary>
    private static void CheckParents(string path)
    {
        for (DirectoryInfo? parent = new(Path.GetDirectoryName(path)!); parent is not null; parent = parent.Parent)
            if (!parent.Exists || (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw Failure();
    }

    /// <summary>Uses the runtime's exclusive stable-lock protocol, with private permissions at Unix file creation.</summary>
    private static FileStream Open(string path, FileMode mode, FileAccess access)
    {
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = FileShare.None, Options = FileOptions.Asynchronous };
        // Windows inherits the presecured parent ACL. This is not a permission grant or hosting security audit.
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new(path, options);
    }

    /// <summary>Provides a fixed value-free diagnostic without preserving potentially sensitive filesystem exceptions.</summary>
    private static InvalidOperationException Failure() =>
        new("AI budget counter could not be created. Check AiQueries:BudgetCounterPath and private directory/state; inspect before retrying.");
}
