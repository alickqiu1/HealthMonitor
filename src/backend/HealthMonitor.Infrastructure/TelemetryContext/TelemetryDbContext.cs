
using HealthMonitor.Domain.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace HealthMonitor.Infrastructure.Data.TelemetryContext;

/// <summary>
/// Provides EF Core persistence for the Telemetry bounded context.
/// </summary>
/// <remarks>
/// Owns the PostgreSQL "telemetry" schema and its four entity tables:
/// simulation batches, telemetry pings, incident logs, and current
/// device telemetry state.
///
/// The Telemetry context is independent of the Device context.
/// Device identifiers are stored as scalar values without foreign
/// keys to the separate Device schema.
///
/// Database credentials, migration execution, and migration history
/// configuration are managed outside this class.
///
/// The database constraints complement domain validation and help
/// protect persisted data against invalid or conflicting writes.
/// </remarks>
public sealed class TelemetryDbContext : DbContext
{
    /// <summary>
    /// Gets the historical telemetry observations.
    /// </summary>
    public DbSet<TelemetryPing> TelemetryPings =>
        Set<TelemetryPing>();

    /// <summary>
    /// Gets the historical and currently open incidents.
    /// </summary>
    public DbSet<IncidentLog> IncidentLogs =>
        Set<IncidentLog>();

    /// <summary>
    /// Gets the latest persisted telemetry state for each observed device.
    /// </summary>
    public DbSet<DeviceTelemetryState> DeviceTelemetryStates =>
        Set<DeviceTelemetryState>();

    /// <summary>
    /// Gets committed simulation receipts used for idempotent replay.
    /// </summary>
    public DbSet<SimulationBatch> SimulationBatches =>
        Set<SimulationBatch>();

    /// <summary>
    /// Initializes the Telemetry persistence context.
    /// </summary>
    /// <param name="options">
    /// Configured EF Core options for the PostgreSQL provider.
    /// </param>
    public TelemetryDbContext(
        DbContextOptions<TelemetryDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Configures all entities owned by the Telemetry schema.
    /// </summary>
    /// <param name="modelBuilder">
    /// EF Core model builder used to configure schema metadata.
    /// </param>
    /// <remarks>
    /// Each entity mapping is maintained in a separate method
    /// to keep constraints, indexes, and relationships together.
    ///
    /// No relationships are configured to the Device bounded context.
    /// </remarks>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("telemetry");

        ConfigureSimulationBatch(modelBuilder);
        ConfigureTelemetryPing(modelBuilder);
        ConfigureIncidentLog(modelBuilder);
        ConfigureDeviceTelemetryState(modelBuilder);
    }

    /// <summary>
    /// Configures committed simulation receipts and their constraints.
    /// </summary>
    /// <param name="modelBuilder">EF Core model builder.</param>
    /// <remarks>
    /// BatchId is application-generated and serves as the receipt key.
    ///
    /// A receipt must have a nonnegative affected-device count
    /// and a commit timestamp that does not precede its request.
    ///
    /// Replay matching and transaction coordination belong to
    /// the application service, not to this mapping.
    /// </remarks>
    private static void ConfigureSimulationBatch(
        ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SimulationBatch>(entity =>
        {
            entity.ToTable(
                "simulation_batches",
                "telemetry",
                table =>
                {
                    table.HasCheckConstraint(
                        "ck_simulation_batches_affected_count",
                        "affected_device_count >= 0");

                    table.HasCheckConstraint(
                        "ck_simulation_batches_commit_time",
                        "committed_at_utc >= requested_at_utc");
                });

            entity.HasKey(e => e.BatchId);

            entity.Property(e => e.BatchId)
                .HasColumnName("batch_id")
                .ValueGeneratedNever();

            entity.Property(e => e.RequestHash)
                .HasColumnName("request_hash")
                .HasMaxLength(64)
                .IsRequired();

            entity.Property(e => e.Scenario)
                .HasColumnName("scenario")
                .IsRequired();

            entity.Property(e => e.RequestedAtUtc)
                .HasColumnName("requested_at_utc")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.Property(e => e.CommittedAtUtc)
                .HasColumnName("committed_at_utc")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.Property(e => e.AffectedDeviceCount)
                .HasColumnName("affected_device_count")
                .IsRequired();
        });
    }

    /// <summary>
    /// Configures historical telemetry observations.
    /// </summary>
    /// <param name="modelBuilder">EF Core model builder.</param>
    /// <remarks>
    /// Success observations require valid latency and CPU values.
    /// Timeout observations must contain neither measurement.
    ///
    /// Each batch can produce at most one observation per device.
    /// The unique batch/device index enforces this rule.
    ///
    /// The history index supports device-specific queries ordered
    /// by observation timestamp and observation identifier.
    ///
    /// A simulation receipt cannot be deleted while retained
    /// observations still reference it.
    /// </remarks>
    private static void ConfigureTelemetryPing(
        ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TelemetryPing>(entity =>
        {
            entity.ToTable(
                "telemetry_pings",
                "telemetry",
                table =>
                {
                    table.HasCheckConstraint(
                        "ck_telemetry_pings_outcome",
                        "outcome IN ('Success', 'Timeout')");

                    table.HasCheckConstraint(
                        "ck_telemetry_pings_measurements",
                        @"(
                            outcome = 'Timeout'
                            AND latency_ms IS NULL
                            AND cpu_percent IS NULL
                          )
                          OR
                          (
                            outcome = 'Success'
                            AND latency_ms IS NOT NULL
                            AND cpu_percent IS NOT NULL
                            AND latency_ms BETWEEN 0 AND 60000
                            AND cpu_percent BETWEEN 0 AND 100
                          )");
                });

            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();

            // Device references remain independent of the Device schema.
            entity.Property(e => e.DeviceId)
                .HasColumnName("device_id")
                .IsRequired();

            entity.Property(e => e.BatchId)
                .HasColumnName("batch_id")
                .IsRequired();

            entity.Property(e => e.ObservedAtUtc)
                .HasColumnName("observed_at_utc")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            // Persist enum names rather than their numeric values.
            entity.Property(e => e.Outcome)
                .HasColumnName("outcome")
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired();

            entity.Property(e => e.LatencyMs)
                .HasColumnName("latency_ms");

            entity.Property(e => e.CpuPercent)
                .HasColumnName("cpu_percent")
                .HasColumnType("numeric(5,2)");

            // Prevent multiple observations for the same device
            // within a single committed simulation batch.
            entity.HasIndex(e => new
                {
                    e.BatchId,
                    e.DeviceId
                })
                .IsUnique()
                .HasDatabaseName(
                    "ux_telemetry_pings_batch_device");

            // Support recent-first history queries per device.
            // Ping ID provides deterministic secondary ordering.
            entity.HasIndex(e => new
                {
                    e.DeviceId,
                    e.ObservedAtUtc,
                    e.Id
                })
                .IsDescending(false, true, true)
                .HasDatabaseName(
                    "ix_telemetry_pings_device_observed_id");

            // Retained observations protect the simulation receipt
            // that records their original committed batch.
            entity.HasOne<SimulationBatch>()
                .WithMany()
                .HasForeignKey(e => e.BatchId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    /// <summary>
    /// Configures incident history, active-incident uniqueness,
    /// and references to opening and resolving observations.
    /// </summary>
    /// <param name="modelBuilder">EF Core model builder.</param>
    /// <remarks>
    /// The partial unique index permits only one unresolved
    /// incident per device and incident kind.
    ///
    /// Resolved incidents remain available for historical queries.
    ///
    /// Opening and resolving ping references use SET NULL deletion
    /// behavior so old telemetry observations can be pruned without
    /// deleting their corresponding incident records.
    /// </remarks>
    private static void ConfigureIncidentLog(
        ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IncidentLog>(entity =>
        {
            entity.ToTable(
                "incident_logs",
                "telemetry",
                table =>
                {
                    table.HasCheckConstraint(
                        "ck_incident_logs_kind",
                        "kind IN ('Latency', 'Cpu', 'Connectivity')");

                    table.HasCheckConstraint(
                        "ck_incident_logs_resolution_time",
                        "resolved_at_utc IS NULL OR " +
                        "resolved_at_utc >= opened_at_utc");
                });

            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();

            entity.Property(e => e.DeviceId)
                .HasColumnName("device_id")
                .IsRequired();

            // Persist stable enum names as text.
            entity.Property(e => e.Kind)
                .HasColumnName("kind")
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();

            entity.Property(e => e.OpenedAtUtc)
                .HasColumnName("opened_at_utc")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.Property(e => e.ResolvedAtUtc)
                .HasColumnName("resolved_at_utc")
                .HasColumnType("timestamp with time zone");

            entity.Property(e => e.OpenedByPingId)
                .HasColumnName("opened_by_ping_id");

            entity.Property(e => e.ResolvedByPingId)
                .HasColumnName("resolved_by_ping_id");

            entity.Property(e => e.Summary)
                .HasColumnName("summary")
                .IsRequired();

            // Support recent-first incident history per device.
            entity.HasIndex(e => new
                {
                    e.DeviceId,
                    e.OpenedAtUtc,
                    e.Id
                })
                .IsDescending(false, true, true)
                .HasDatabaseName(
                    "ix_incident_logs_device_opened_id");

            // Permit only one open incident for each device
            // and kind while retaining resolved incident history.
            entity.HasIndex(e => new
                {
                    e.DeviceId,
                    e.Kind
                })
                .IsUnique()
                .HasFilter("resolved_at_utc IS NULL")
                .HasDatabaseName(
                    "ux_incident_logs_open_device_kind");

            // Historical incident records survive pruning of
            // the observations that originally opened them.
            entity.HasOne<TelemetryPing>()
                .WithMany()
                .HasForeignKey(e => e.OpenedByPingId)
                .OnDelete(DeleteBehavior.SetNull);

            // Historical incident records also survive pruning
            // of the observations that resolved them.
            entity.HasOne<TelemetryPing>()
                .WithMany()
                .HasForeignKey(e => e.ResolvedByPingId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }

    /// <summary>
    /// Configures the current persisted telemetry state for each device.
    /// </summary>
    /// <param name="modelBuilder">EF Core model builder.</param>
    /// <remarks>
    /// DeviceId is the state record's primary key.
    ///
    /// A successful latest observation requires latency and CPU
    /// measurements; a timeout requires both to be null.
    ///
    /// The last successful observation cannot be later than the
    /// latest observation recorded in the state.
    ///
    /// The latest observation is protected from retention deletion
    /// through a restrictive foreign-key relationship.
    /// </remarks>
    private static void ConfigureDeviceTelemetryState(
        ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeviceTelemetryState>(entity =>
        {
            entity.ToTable(
                "device_telemetry_state",
                "telemetry",
                table =>
                {
                    table.HasCheckConstraint(
                        "ck_device_state_outcome",
                        "latest_outcome IN ('Success', 'Timeout')");

                    table.HasCheckConstraint(
                        "ck_device_state_measurements",
                        @"(
                            latest_outcome = 'Timeout'
                            AND latest_latency_ms IS NULL
                            AND latest_cpu_percent IS NULL
                          )
                          OR
                          (
                            latest_outcome = 'Success'
                            AND latest_latency_ms IS NOT NULL
                            AND latest_cpu_percent IS NOT NULL
                            AND latest_latency_ms BETWEEN 0 AND 60000
                            AND latest_cpu_percent BETWEEN 0 AND 100
                          )");

                    table.HasCheckConstraint(
                        "ck_device_state_success_time",
                        "last_successful_at_utc IS NULL OR " +
                        "last_successful_at_utc <= last_observed_at_utc");
                });

            entity.HasKey(e => e.DeviceId);

            entity.Property(e => e.DeviceId)
                .HasColumnName("device_id")
                .ValueGeneratedNever();

            entity.Property(e => e.LastPingId)
                .HasColumnName("last_ping_id")
                .IsRequired();

            entity.Property(e => e.LastObservedAtUtc)
                .HasColumnName("last_observed_at_utc")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.Property(e => e.LastSuccessfulAtUtc)
                .HasColumnName("last_successful_at_utc")
                .HasColumnType("timestamp with time zone");

            entity.Property(e => e.LatestOutcome)
                .HasColumnName("latest_outcome")
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired();

            entity.Property(e => e.LatestLatencyMs)
                .HasColumnName("latest_latency_ms");

            entity.Property(e => e.LatestCpuPercent)
                .HasColumnName("latest_cpu_percent")
                .HasColumnType("numeric(5,2)");

            // Prevent retention pruning from removing the
            // observation currently referenced by device state.
            entity.HasOne<TelemetryPing>()
                .WithMany()
                .HasForeignKey(e => e.LastPingId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
