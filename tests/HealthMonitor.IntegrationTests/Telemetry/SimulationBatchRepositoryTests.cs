
using System;
using System.Data;
using System.Threading.Tasks;
using HealthMonitor.Application.Telemetry;
using HealthMonitor.Domain.Telemetry;
using HealthMonitor.Infrastructure.Data.TelemetryContext;
using HealthMonitor.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace HealthMonitor.IntegrationTests.Telemetry;

/// <summary>
/// Verifies the PostgreSQL persistence behavior of
/// SimulationBatchRepository.
/// </summary>
/// <remarks>
/// These tests exercise real EF Core operations against an
/// explicitly authorized, isolated PostgreSQL database.
///
/// The tests verify:
///
/// - Receipt lookup by batch identifier.
/// - Missing receipt behavior.
/// - Change tracking without implicit persistence.
/// - Transaction commit and rollback.
/// - Database uniqueness enforcement.
/// - READ COMMITTED visibility across connections.
/// - Validation of null repository arguments.
///
/// Each test uses independently generated receipt identifiers.
/// Tests that commit records remove only their own data.
///
/// The database schema must already have been initialized using
/// the existing Telemetry EF Core migrations.
///
/// The tests do not create or drop schemas, apply migrations,
/// or modify development and production Supabase databases.
/// </remarks>
public sealed class SimulationBatchRepositoryTests
{
    private static readonly DateTimeOffset RequestedAtUtc =
        new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset CommittedAtUtc =
        RequestedAtUtc.AddSeconds(2);

    private readonly PostgreSqlTestFixture _fixture = new();

    /// <summary>
    /// Creates a valid simulation receipt with a unique identifier.
    /// </summary>
    /// <param name="batchId">
    /// Optional receipt identifier. Null generates a new GUID.
    /// </param>
    /// <returns>A validated committed receipt domain entity.</returns>
    /// <remarks>
    /// The hash is a deterministic, valid 64-character hexadecimal
    /// value. These tests verify repository persistence rather than
    /// request canonicalization.
    ///
    /// Guid.NewGuid is sufficient here because the repository
    /// does not perform UUIDv7 freshness validation. That rule
    /// belongs to the ingestion application service.
    /// </remarks>
    private static SimulationBatch CreateReceipt(Guid? batchId = null)
    {
        return SimulationBatch.CreateCommitted(
            batchId ?? Guid.NewGuid(),
            new string('a', 64),
            "Healthy",
            RequestedAtUtc,
            CommittedAtUtc,
            1);
    }

    /// <summary>
    /// Creates an EF Core Telemetry context using an already
    /// authorized PostgreSQL test connection.
    /// </summary>
    /// <param name="connection">
    /// Connection opened and validated by PostgreSqlTestFixture.
    /// </param>
    /// <returns>
    /// A context configured to use the supplied connection.
    /// </returns>
    /// <remarks>
    /// The context does not own or independently open the
    /// PostgreSQL connection.
    ///
    /// Reusing the validated connection prevents the repository
    /// from accidentally obtaining a normal application or
    /// Supabase connection string.
    ///
    /// The caller must dispose both the context and connection.
    /// </remarks>
    private static TelemetryDbContext CreateContext(
        NpgsqlConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var options =
            new DbContextOptionsBuilder<TelemetryDbContext>()
                .UseNpgsql(connection)
                .Options;

        return new TelemetryDbContext(options);
    }

    /// <summary>
    /// Removes only the receipt created by the current test.
    /// </summary>
    /// <param name="batchId">Unique test receipt identifier.</param>
    /// <returns>A task representing cleanup completion.</returns>
    /// <remarks>
    /// Committed data must be removed after tests that exercise
    /// commit behavior.
    ///
    /// Cleanup deliberately avoids TRUNCATE, schema deletion,
    /// and broad DELETE statements because other tests may
    /// operate against the same isolated database concurrently.
    ///
    /// Test-created receipts have no dependent telemetry pings.
    /// </remarks>
    private async Task DeleteTestReceiptAsync(Guid batchId)
    {
        await using NpgsqlConnection connection =
            await _fixture.OpenConnectionAsync();

        await using TelemetryDbContext context =
            CreateContext(connection);

        SimulationBatch? receipt =
            await context.Set<SimulationBatch>()
                .SingleOrDefaultAsync(x => x.BatchId == batchId);

        if (receipt is null)
            return;

        context.Set<SimulationBatch>().Remove(receipt);

        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Constructor_NullContext_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new SimulationBatchRepository(null!));

        await Task.CompletedTask;
    }

    [Fact]
    public void Add_NullReceipt_Throws()
    {
        // Argument validation occurs before EF Core touches
        // the database. A context is still required to construct
        // the repository, but no connection must be opened.
        var options =
            new DbContextOptionsBuilder<TelemetryDbContext>()
                .UseNpgsql(
                    "Host=127.0.0.1;" +
                    "Database=healthmonitor_test;" +
                    "Username=unused")
                .Options;

        using var context = new TelemetryDbContext(options);

        var repository = new SimulationBatchRepository(context);

        Assert.Throws<ArgumentNullException>(
            () => repository.Add(null!));
    }

    [Fact]
    public async Task FindByIdAsync_MissingReceipt_ReturnsNull()
    {
        await using NpgsqlConnection connection =
            await _fixture.OpenConnectionAsync();

        await using TelemetryDbContext context =
            CreateContext(connection);

        var repository = new SimulationBatchRepository(context);

        SimulationBatch? result =
            await repository.FindByIdAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task Add_StagesReceiptWithoutSaving()
    {
        await using NpgsqlConnection connection =
            await _fixture.OpenConnectionAsync();

        await using TelemetryDbContext context =
            CreateContext(connection);

        var repository = new SimulationBatchRepository(context);

        SimulationBatch receipt = CreateReceipt();

        repository.Add(receipt);

        // Add must only stage the receipt in EF Core.
        // Saving independently would break batch atomicity.
        Assert.Equal(
            EntityState.Added,
            context.Entry(receipt).State);

        // A database lookup must not return a receipt that
        // only exists in the current context's change tracker.
        SimulationBatch? persisted =
            await repository.FindByIdAsync(receipt.BatchId);

        Assert.Null(persisted);
    }

    [Fact]
    public async Task FindByIdAsync_ExistingReceipt_ReturnsStoredValues()
    {
        SimulationBatch receipt = CreateReceipt();

        try
        {
            await using (NpgsqlConnection connection =
                await _fixture.OpenConnectionAsync())
            {
                await using TelemetryDbContext context =
                    CreateContext(connection);

                await context.Set<SimulationBatch>()
                    .AddAsync(receipt);

                await context.SaveChangesAsync();
            }

            // Use a different connection and context so the
            // assertion cannot be satisfied by EF tracking.
            await using NpgsqlConnection readConnection =
                await _fixture.OpenConnectionAsync();

            await using TelemetryDbContext readContext =
                CreateContext(readConnection);

            var repository =
                new SimulationBatchRepository(readContext);

            SimulationBatch? found =
                await repository.FindByIdAsync(receipt.BatchId);

            Assert.NotNull(found);

            Assert.Equal(receipt.BatchId, found.BatchId);
            Assert.Equal(receipt.RequestHash, found.RequestHash);
            Assert.Equal(receipt.Scenario, found.Scenario);
            Assert.Equal(receipt.RequestedAtUtc, found.RequestedAtUtc);
            Assert.Equal(receipt.CommittedAtUtc, found.CommittedAtUtc);
            Assert.Equal(
                receipt.AffectedDeviceCount,
                found.AffectedDeviceCount);

            // Receipt lookups are read-only and should not
            // attach a tracked entity to the querying context.
            Assert.Equal(
                EntityState.Detached,
                readContext.Entry(found).State);
        }
        finally
        {
            await DeleteTestReceiptAsync(receipt.BatchId);
        }
    }

    [Fact]
    public async Task Transaction_Commit_MakesReceiptVisible()
    {
        SimulationBatch receipt = CreateReceipt();

        try
        {
            await using NpgsqlConnection writeConnection =
                await _fixture.OpenConnectionAsync();

            await using TelemetryDbContext writeContext =
                CreateContext(writeConnection);

            await using var transaction =
                await writeContext.Database.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted);

            var repository =
                new SimulationBatchRepository(writeContext);

            repository.Add(receipt);

            await writeContext.SaveChangesAsync();

            // A separate connection cannot see the uncommitted
            // receipt, even after EF Core has executed INSERT.
            await using NpgsqlConnection readConnection =
                await _fixture.OpenConnectionAsync();

            await using TelemetryDbContext readContext =
                CreateContext(readConnection);

            var reader =
                new SimulationBatchRepository(readContext);

            Assert.Null(
                await reader.FindByIdAsync(receipt.BatchId));

            await transaction.CommitAsync();

            // Once committed, the same independent reader
            // must be able to retrieve the receipt.
            SimulationBatch? committed =
                await reader.FindByIdAsync(receipt.BatchId);

            Assert.NotNull(committed);
            Assert.Equal(receipt.BatchId, committed.BatchId);
        }
        finally
        {
            await DeleteTestReceiptAsync(receipt.BatchId);
        }
    }

    [Fact]
    public async Task Transaction_Rollback_DoesNotPersistReceipt()
    {
        SimulationBatch receipt = CreateReceipt();

        await using (NpgsqlConnection connection =
            await _fixture.OpenConnectionAsync())
        {
            await using TelemetryDbContext context =
                CreateContext(connection);

            await using var transaction =
                await context.Database.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted);

            var repository =
                new SimulationBatchRepository(context);

            repository.Add(receipt);

            await context.SaveChangesAsync();

            // SaveChanges writes inside the transaction.
            // Rolling back must discard the database insert.
            await transaction.RollbackAsync();
        }

        await using NpgsqlConnection verifyConnection =
            await _fixture.OpenConnectionAsync();

        await using TelemetryDbContext verifyContext =
            CreateContext(verifyConnection);

        var verificationRepository =
            new SimulationBatchRepository(verifyContext);

        Assert.Null(
            await verificationRepository.FindByIdAsync(
                receipt.BatchId));
    }

    [Fact]
    public async Task DuplicateBatchId_IsRejectedByPostgreSql()
    {
        SimulationBatch first = CreateReceipt();

        // A second domain object with the same BatchId
        // represents a conflicting attempt to persist another
        // receipt using an existing database primary key.
        SimulationBatch duplicate =
            CreateReceipt(first.BatchId);

        try
        {
            await using NpgsqlConnection firstConnection =
                await _fixture.OpenConnectionAsync();

            await using TelemetryDbContext firstContext =
                CreateContext(firstConnection);

            firstContext.Set<SimulationBatch>().Add(first);
            await firstContext.SaveChangesAsync();

            await using NpgsqlConnection secondConnection =
                await _fixture.OpenConnectionAsync();

            await using TelemetryDbContext secondContext =
                CreateContext(secondConnection);

            var repository =
                new SimulationBatchRepository(secondContext);

            repository.Add(duplicate);

            // PostgreSQL's primary key must enforce uniqueness
            // even if an application caller misses a replay check.
            //
            // This is a persistence safeguard, not a substitute
            // for the ingestion service's receipt-first logic.
            await Assert.ThrowsAsync<DbUpdateException>(
                () => secondContext.SaveChangesAsync());
        }
        finally
        {
            await DeleteTestReceiptAsync(first.BatchId);
        }
    }

    [Fact]
    public async Task ReadCommitted_SeesReceiptAfterAnotherTransactionCommits()
    {
        SimulationBatch receipt = CreateReceipt();

        try
        {
            await using NpgsqlConnection writerConnection =
                await _fixture.OpenConnectionAsync();

            await using TelemetryDbContext writerContext =
                CreateContext(writerConnection);

            await using var writerTransaction =
                await writerContext.Database.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted);

            await using NpgsqlConnection readerConnection =
                await _fixture.OpenConnectionAsync();

            await using TelemetryDbContext readerContext =
                CreateContext(readerConnection);

            await using var readerTransaction =
                await readerContext.Database.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted);

            var reader =
                new SimulationBatchRepository(readerContext);

            // The reader transaction begins before the writer
            // commits. Its first receipt query observes absence.
            Assert.Null(
                await reader.FindByIdAsync(receipt.BatchId));

            var writer =
                new SimulationBatchRepository(writerContext);

            writer.Add(receipt);

            await writerContext.SaveChangesAsync();

            await writerTransaction.CommitAsync();

            // READ COMMITTED gives each statement a fresh
            // snapshot. The reader's second query must see
            // a receipt committed after its first query.
            //
            // This behavior supports the ingestion service's
            // second receipt lookup after advisory-lock
            // acquisition.
            SimulationBatch? found =
                await reader.FindByIdAsync(receipt.BatchId);

            Assert.NotNull(found);
            Assert.Equal(receipt.BatchId, found.BatchId);

            await readerTransaction.CommitAsync();
        }
        finally
        {
            await DeleteTestReceiptAsync(receipt.BatchId);
        }
    }
}
