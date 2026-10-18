using HealthMonitor.Api.Provisioning;
using HealthMonitor.Domain.Telemetry;

namespace HealthMonitor.Api.Fleet;

/// <summary>Validated API grid input, independent of HTTP binding and persistence.</summary>
/// <remarks>
/// The initial site/type filter allowlists come from the fixed catalog, not the current active rows.
/// They are API filter capabilities, not new inventory invariants: other-site rows remain visible unfiltered.
/// Supporting another site filter requires a reviewed allowlist extension. Only empty trimmed search is neutral;
/// supplied blank allowlisted tokens are invalid. No model-generated property name or expression is accepted.
/// </remarks>
internal sealed class DeviceGridQuery
{
    private static readonly HashSet<string> Sites = FixedDeviceCatalog.Devices.Select(d => d.SiteCode).ToHashSet(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> Types = FixedDeviceCatalog.Devices.Select(d => d.DeviceType)
        .Distinct(StringComparer.Ordinal).ToDictionary(type => type, StringComparer.OrdinalIgnoreCase);

    /// <summary>Validates and normalizes the allowlisted grid request before any module query.</summary>
    /// <param name="page">One-based positive Int32 page, default one.</param>
    /// <param name="pageSize">One through 100, default 25.</param>
    /// <param name="q">Trimmed literal name/tag search, at most 100 UTF-16 code units; empty becomes null.</param>
    /// <param name="site">Optional supported site; trim and uppercase before exact matching.</param>
    /// <param name="type">Optional supported category; trim and canonicalize case.</param>
    /// <param name="status">Optional exact canonical health token after trimming; numeric values are invalid.</param>
    /// <param name="sort">One of seven explicit lower-camel keys; omitted means displayName.</param>
    /// <param name="direction">asc or desc after trimming; omitted means asc.</param>
    /// <exception cref="ArgumentException">Search or an allowlisted token is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Paging is outside supported bounds.</exception>
    internal DeviceGridQuery(int page = 1, int pageSize = 25, string? q = null, string? site = null,
        string? type = null, string? status = null, string? sort = null, string? direction = null)
    {
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        if (pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(pageSize));
        Page = page; PageSize = pageSize;
        Q = q?.Trim();
        if (Q?.Length > 100) throw new ArgumentException("Search must contain at most 100 characters after trimming.", nameof(q));
        if (Q?.Length == 0) Q = null;
        if (site is not null)
        {
            Site = site.Trim().ToUpperInvariant();
            if (!Sites.Contains(Site)) throw new ArgumentException("Unsupported site filter.", nameof(site));
        }
        if (type is not null)
        {
            if (!Types.TryGetValue(type.Trim(), out var canonical)) throw new ArgumentException("Unsupported type filter.", nameof(type));
            Type = canonical;
        }
        Status = status?.Trim() switch
        {
            null => null,
            "Unknown" => HealthState.Unknown,
            "Healthy" => HealthState.Healthy,
            "Degraded" => HealthState.Degraded,
            "Offline" => HealthState.Offline,
            _ => throw new ArgumentException("Unsupported status filter.", nameof(status))
        };
        Sort = (sort ?? "displayName").Trim() switch
        {
            "displayName" => DeviceGridSort.DisplayName,
            "assetTag" => DeviceGridSort.AssetTag,
            "siteCode" => DeviceGridSort.SiteCode,
            "status" => DeviceGridSort.Status,
            "lastObservedAtUtc" => DeviceGridSort.LastObservedAtUtc,
            "latencyMs" => DeviceGridSort.LatencyMs,
            "cpuPercent" => DeviceGridSort.CpuPercent,
            _ => throw new ArgumentException("Unsupported sort key.", nameof(sort))
        };
        Descending = (direction ?? "asc").Trim() switch
        {
            "asc" => false, "desc" => true,
            _ => throw new ArgumentException("Unsupported sort direction.", nameof(direction))
        };
    }

    /// <summary>One-based requested page.</summary>
    internal int Page { get; }
    /// <summary>Maximum rows returned.</summary>
    internal int PageSize { get; }
    /// <summary>Overflow-safe offset; bounded result size is checked before narrowing.</summary>
    internal long Offset => ((long)Page - 1) * PageSize;
    /// <summary>Literal search or null.</summary>
    internal string? Q { get; }
    /// <summary>Canonical supported site or null.</summary>
    internal string? Site { get; }
    /// <summary>Canonical supported category or null.</summary>
    internal string? Type { get; }
    /// <summary>Optional Domain health selection; never recomputed during filtering.</summary>
    internal HealthState? Status { get; }
    /// <summary>Validated sort operation, not an arbitrary property name.</summary>
    internal DeviceGridSort Sort { get; }
    /// <summary>Whether the primary sort is reversed; UUID ties remain ascending.</summary>
    internal bool Descending { get; }
}

/// <summary>Internal allowlist of supported comparison operations, independent of property reflection.</summary>
internal enum DeviceGridSort
{
    /// <summary>Ordinal-ignore-case display name.</summary>
    DisplayName,
    /// <summary>Ordinal-ignore-case asset tag.</summary>
    AssetTag,
    /// <summary>Ordinal-ignore-case site code.</summary>
    SiteCode,
    /// <summary>Explicit Offline/Degraded/Unknown/Healthy severity.</summary>
    Status,
    /// <summary>Chronological observation time; nulls last.</summary>
    LastObservedAtUtc,
    /// <summary>Numeric latency; nulls last.</summary>
    LatencyMs,
    /// <summary>Numeric CPU percentage; nulls last.</summary>
    CpuPercent
}
