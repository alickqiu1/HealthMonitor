using System.Data.Common;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HealthMonitor.Api.Health;

/// <summary>Checks connectivity through a borrowed runtime data source without accessing either module's tables.</summary>
/// <remarks>
/// SELECT 1 forces a server round trip even when a connection is leased from the pool. Success does not prove
/// schema readiness, role privileges, hosted certificate trust, or deployment acceptance. The caller owns the pool.
/// The health-check service supplies a cooperative cancellation budget; it is not a hard execution deadline.
/// </remarks>
internal sealed class DatabaseReadinessCheck : IHealthCheck
{
    private readonly DbDataSource source;

    /// <summary>Borrows the existing shared pool; the BCL contract also permits database-free failure tests.</summary>
    /// <param name="source">Runtime pool, never owned or disposed by this check.</param>
    /// <exception cref="ArgumentNullException">Source is null.</exception>
    internal DatabaseReadinessCheck(DbDataSource source) => this.source = source ?? throw new ArgumentNullException(nameof(source));

    /// <summary>Leases a connection, executes one bounded scalar command, and disposes only the lease and command.</summary>
    /// <param name="context">Framework registration context; no report details are exposed publicly.</param>
    /// <param name="cancellationToken">Linked request/check timeout cancellation supplied by the framework.</param>
    /// <returns>Healthy only for the expected scalar and successful resource disposal; otherwise a fixed safe failure.</returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested, including when cleanup also fails.</exception>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = 5;
            object? value = await command.ExecuteScalarAsync(cancellationToken);
            return value is int number && number == 1 ? HealthCheckResult.Healthy() : Unavailable();
        }
        catch (Exception)
        {
            // Disposal can mask an earlier cancellation; retain cancellation without retaining a raw provider exception.
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            // Framework logging must never receive provider exception objects, SQL, or connection details.
            return Unavailable();
        }
    }

    /// <summary>Creates the fixed failure report without an exception or provider-controlled description/data.</summary>
    private static HealthCheckResult Unavailable() => HealthCheckResult.Unhealthy("Database connectivity unavailable.");
}
