
using System;
using System.Threading;
using System.Threading.Tasks;
using HealthMonitor.Infrastructure.Data.TelemetryContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace HealthMonitor.Infrastructure.Data.TelemetryContext;

/// <summary>
/// Acquires the PostgreSQL transaction-scoped advisory lock
/// shared by telemetry ingestion and retention operations.
/// </summary>
/// <remarks>
/// The lock serializes operations that change telemetry state,
/// incident lifecycles, and simulation receipts.
///
/// A database advisory lock is used instead of an in-process
/// semaphore because multiple API instances may access the
/// same PostgreSQL database.
///
/// The caller must begin a TelemetryDbContext transaction before
/// acquiring the lock and must commit or roll back that transaction
/// after completing the protected work.
///
/// PostgreSQL automatically releases the transaction-scoped lock
/// when its owning transaction ends.
///
/// This component does not create or commit transactions and does
/// not modify telemetry tables.
/// </remarks>
public static class TelemetryIngestionLock
{
    // All ingestion and retention callers must use the same
    // stable key. Changing it independently in either workflow
    // would eliminate their mutual exclusion guarantee.
    //
    // This application-specific key must not be reused by
    // unrelated advisory-lock workflows.
    private const long AdvisoryLockKey = 7142026001L;

    /// <summary>
    /// Acquires the shared advisory lock within an active
    /// PostgreSQL transaction.
    /// </summary>
    /// <param name="context">Active Telemetry persistence context.</param>
    /// <param name="cancellationToken">
    /// Cancellation token for database operations.
    /// </param>
    /// <returns>A task that completes after acquiring the lock.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when context is null.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no EF Core transaction is active.
    /// </exception>
    /// <exception cref="PostgresException">
    /// Thrown when PostgreSQL cannot acquire the lock or
    /// another database error occurs. Lock contention produces
    /// SQLSTATE 55P03 (lock_not_available).
    /// </exception>
    /// <remarks>
    /// The maximum database lock wait is two seconds.
    ///
    /// A transaction-scoped advisory lock ensures that only
    /// one cooperating ingestion or retention operation
    /// enters the critical section at a time.
    ///
    /// The HTTP application layer is responsible for translating
    /// lock contention into a service-unavailable response with
    /// an appropriate Retry-After header.
    ///
    /// The caller must not catch a failed lock acquisition and
    /// continue using the failed transaction. It must roll back
    /// or dispose that transaction.
    /// </remarks>
    public static async Task AcquireAsync(
        TelemetryDbContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        IDbContextTransaction? transaction =
            context.Database.CurrentTransaction;

        if (transaction is null)
        {
            throw new InvalidOperationException(
                "An active telemetry transaction is required " +
                "before acquiring the advisory lock.");
        }

        // Restrict lock waiting to two seconds so concurrent
        // simulation requests do not queue indefinitely.
        //
        // SET LOCAL applies only to the current transaction.
        // It does not permanently change the pooled connection
        // or affect unrelated requests after the transaction ends.
        await context.Database.ExecuteSqlRawAsync(
            "SET LOCAL lock_timeout = '2s'",
            cancellationToken);

        // Acquire the lock on the same connection and transaction
        // used by EF Core. A separate connection would not protect
        // the subsequent telemetry writes.
        //
        // pg_advisory_xact_lock is automatically released at
        // transaction commit or rollback.
        await context.Database.ExecuteSqlRawAsync(
            $"SELECT pg_advisory_xact_lock({AdvisoryLockKey})",
            cancellationToken);
    }
}
