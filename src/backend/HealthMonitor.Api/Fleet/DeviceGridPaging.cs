using HealthMonitor.Domain.Telemetry;

namespace HealthMonitor.Api.Fleet;

/// <summary>Applies global grid operations to an already composed fleet without changing its rows, health, clock, or global metrics.</summary>
internal static class DeviceGridPaging
{
    /// <summary>Filters globally, sorts a private copy, counts, and only then slices the requested page.</summary>
    /// <param name="snapshot">Complete bounded active-fleet snapshot with already classified health.</param>
    /// <param name="query">Validated allowlisted input.</param>
    /// <returns>Frozen page with the true filtered count and the original evaluation instant.</returns>
    /// <exception cref="ArgumentNullException">Snapshot or query is null.</exception>
    internal static DeviceGridPage Apply(FleetSnapshot snapshot, DeviceGridQuery query)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(query);
        var rows = snapshot.Rows.Where(row =>
            (query.Q is null || row.DisplayName.Contains(query.Q, StringComparison.OrdinalIgnoreCase) ||
                row.AssetTag.Contains(query.Q, StringComparison.OrdinalIgnoreCase)) &&
            (query.Site is null || string.Equals(row.SiteCode, query.Site, StringComparison.Ordinal)) &&
            (query.Type is null || string.Equals(row.DeviceType, query.Type, StringComparison.Ordinal)) &&
            (query.Status is null || row.Status == query.Status)).ToArray();
        Array.Sort(rows, (left, right) => Compare(left, right, query));
        // Even a valid Int32 page can produce a much larger offset. The bounded fleet makes empty-page detection cheap.
        var items = query.Offset >= rows.Length ? Array.Empty<FleetRow>() :
            rows.Skip((int)query.Offset).Take(query.PageSize).ToArray();
        return new(snapshot.AsOfUtc, Array.AsReadOnly(items), query.Page, query.PageSize, rows.Length);
    }

    /// <summary>Compares typed primary values and applies an ascending UUID tie independently of direction.</summary>
    /// <param name="left">First immutable row.</param>
    /// <param name="right">Second immutable row.</param>
    /// <param name="query">Validated sort and direction.</param>
    /// <returns>Primary comparison or stable UUID comparison when equal.</returns>
    private static int Compare(FleetRow left, FleetRow right, DeviceGridQuery query)
    {
        int result = query.Sort switch
        {
            DeviceGridSort.DisplayName => CompareText(left.DisplayName, right.DisplayName, query.Descending),
            DeviceGridSort.AssetTag => CompareText(left.AssetTag, right.AssetTag, query.Descending),
            DeviceGridSort.SiteCode => CompareText(left.SiteCode, right.SiteCode, query.Descending),
            DeviceGridSort.Status => query.Descending ? Rank(right.Status).CompareTo(Rank(left.Status)) : Rank(left.Status).CompareTo(Rank(right.Status)),
            DeviceGridSort.LastObservedAtUtc => CompareOptional(left.LastObservedAtUtc, right.LastObservedAtUtc, query.Descending),
            DeviceGridSort.LatencyMs => CompareOptional(left.LatencyMs, right.LatencyMs, query.Descending),
            DeviceGridSort.CpuPercent => CompareOptional(left.CpuPercent, right.CpuPercent, query.Descending),
            _ => throw new InvalidOperationException("Unsupported validated grid sort.")
        };
        return result != 0 ? result : left.DeviceId.CompareTo(right.DeviceId);
    }

    /// <summary>Uses culture-independent text ordering; swapping operands avoids relying on comparison magnitude.</summary>
    /// <param name="left">First inventory text.</param>
    /// <param name="right">Second inventory text.</param>
    /// <param name="descending">Whether to swap the comparison operands.</param>
    /// <returns>Culture-independent primary comparison, before the UUID tie-breaker.</returns>
    private static int CompareText(string left, string right, bool descending) => descending
        ? StringComparer.OrdinalIgnoreCase.Compare(right, left) : StringComparer.OrdinalIgnoreCase.Compare(left, right);

    /// <summary>Keeps absent measurements last in both directions; only present typed values reverse.</summary>
    /// <typeparam name="T">Chronological or numeric value type.</typeparam>
    /// <param name="left">First optional value.</param>
    /// <param name="right">Second optional value.</param>
    /// <param name="descending">Whether present values reverse.</param>
    /// <returns>Null-last comparison independent of direction, or typed comparison.</returns>
    private static int CompareOptional<T>(T? left, T? right, bool descending) where T : struct, IComparable<T>
    {
        if (!left.HasValue) return right.HasValue ? 1 : 0;
        if (!right.HasValue) return -1;
        return descending ? right.Value.CompareTo(left.Value) : left.Value.CompareTo(right.Value);
    }

    /// <summary>Defines product severity explicitly rather than using Domain enum numeric ordering.</summary>
    /// <param name="state">Previously computed Domain health.</param>
    /// <returns>Ascending severity position required by Edition 1.5.</returns>
    /// <exception cref="InvalidOperationException">A row contains undefined health.</exception>
    private static int Rank(HealthState state) => state switch
    {
        HealthState.Offline => 0, HealthState.Degraded => 1, HealthState.Unknown => 2, HealthState.Healthy => 3,
        _ => throw new InvalidOperationException("Grid row has undefined health.")
    };
}

/// <summary>Internal filtered page, distinct from unchanged global metrics and future HTTP DTOs.</summary>
/// <param name="AsOfUtc">Original snapshot's evaluation instant.</param>
/// <param name="Items">Defensively frozen selected rows.</param>
/// <param name="Page">Requested one-based page, including out-of-range requests.</param>
/// <param name="PageSize">Requested page capacity.</param>
/// <param name="TotalCount">Count after all filters and before pagination.</param>
internal sealed record DeviceGridPage(DateTimeOffset AsOfUtc, IReadOnlyList<FleetRow> Items, int Page, int PageSize, int TotalCount);
