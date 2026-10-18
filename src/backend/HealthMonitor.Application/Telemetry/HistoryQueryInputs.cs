namespace HealthMonitor.Application.Telemetry;

/// <summary>Validated server-page coordinates shared by the two concrete Telemetry history operations.</summary>
public sealed class HistoryPaging
{
    /// <summary>Creates a one-based page without imposing an undocumented upper page bound.</summary>
    /// <param name="page">One-based positive Int32 page.</param>
    /// <param name="pageSize">One through 100 rows.</param>
    /// <exception cref="ArgumentOutOfRangeException">Page or size is outside the supported bounds.</exception>
    public HistoryPaging(int page = 1, int pageSize = 25)
    {
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        if (pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(pageSize));
        Page = page; PageSize = pageSize;
    }
    /// <summary>One-based requested page.</summary>
    public int Page { get; }
    /// <summary>Maximum rows to return.</summary>
    public int PageSize { get; }
    /// <summary>Int64 offset prevents overflow for valid large page/size combinations.</summary>
    public long Offset => ((long)Page - 1) * PageSize;
}

/// <summary>Validated half-open UTC telemetry window; incident queries deliberately have no date window.</summary>
public sealed class TelemetryHistoryRange
{
    /// <summary>Creates a nonempty UTC window of at most seven days, including exact seven-day equality.</summary>
    /// <param name="fromUtc">Inclusive non-default zero-offset start.</param>
    /// <param name="toUtc">Exclusive non-default zero-offset end after start.</param>
    /// <exception cref="ArgumentException">UTC timestamps, ordering, or the seven-day maximum are invalid.</exception>
    public TelemetryHistoryRange(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        RequireUtc(fromUtc, nameof(fromUtc)); RequireUtc(toUtc, nameof(toUtc));
        if (fromUtc >= toUtc || toUtc - fromUtc > TimeSpan.FromDays(7))
            throw new ArgumentException("Telemetry history requires an increasing UTC range of at most seven days.");
        FromUtc = fromUtc; ToUtc = toUtc;
    }
    /// <summary>Inclusive start.</summary>
    public DateTimeOffset FromUtc { get; }
    /// <summary>Exclusive end.</summary>
    public DateTimeOffset ToUtc { get; }

    /// <summary>Resolves omitted endpoints against one evaluation instant, without reading a clock itself.</summary>
    /// <param name="asOfUtc">Captured non-default zero-offset instant.</param>
    /// <param name="fromUtc">Optional inclusive start; otherwise effective end minus 24 hours.</param>
    /// <param name="toUtc">Optional exclusive end; otherwise the captured instant.</param>
    /// <returns>A validated range; both endpoints absent means the last 24 hours.</returns>
    /// <exception cref="ArgumentException">UTC, ordering, duration, or default-window subtraction is invalid.</exception>
    public static TelemetryHistoryRange ForRequest(DateTimeOffset asOfUtc, DateTimeOffset? fromUtc, DateTimeOffset? toUtc)
    {
        RequireUtc(asOfUtc, nameof(asOfUtc));
        var end = toUtc ?? asOfUtc;
        RequireUtc(end, nameof(toUtc));
        if (fromUtc is null && end.UtcTicks < TimeSpan.TicksPerDay)
            throw new ArgumentException("The default telemetry window would precede the supported timestamp range.", nameof(toUtc));
        return new(fromUtc ?? end.AddDays(-1), end);
    }

    /// <summary>Validates the same timestamp representation used by persisted observations.</summary>
    /// <param name="value">Timestamp to check.</param>
    /// <param name="name">Owned parameter name.</param>
    /// <exception cref="ArgumentException">Timestamp is default or not zero-offset UTC.</exception>
    private static void RequireUtc(DateTimeOffset value, string name)
    {
        if (value == default || value.Offset != TimeSpan.Zero) throw new ArgumentException("A valid UTC timestamp is required.", name);
    }
}

/// <summary>Allowlisted incident membership; there is no telemetry-style date filter.</summary>
public enum IncidentHistoryState
{
    /// <summary>Both open and resolved incidents.</summary>
    All,
    /// <summary>Only incidents without a resolution timestamp.</summary>
    Open,
    /// <summary>Only incidents with a resolution timestamp.</summary>
    Resolved
}
