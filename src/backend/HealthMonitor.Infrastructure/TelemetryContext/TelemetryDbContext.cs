
using HealthMonitor.Domain.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace HealthMonitor.Infrastructure.Data.TelemetryContext;

/// <summary>
/// EF Core context for telemetry observations, incidents,
/// current device telemetry state, and simulation receipts.
/// Owns the PostgreSQL telemetry schema.
/// </summary>
public sealed class TelemetryDbContext : DbContext
{
    public DbSet<TelemetryPing> TelemetryPings =>
        Set<TelemetryPing>();

    public DbSet<IncidentLog> IncidentLogs =>
        Set<IncidentLog>();

    public DbSet<DeviceTelemetryState> DeviceTelemetryStates =>
        Set<DeviceTelemetryState>();

    public DbSet<SimulationBatch> SimulationBatches =>
        Set<SimulationBatch>();

    public TelemetryDbContext(
        DbContextOptions<TelemetryDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("telemetry");

        ConfigureSimulationBatch(modelBuilder);
        ConfigureTelemetryPing(modelBuilder);
        ConfigureIncidentLog(modelBuilder);
        ConfigureDeviceTelemetryState(modelBuilder);
    }

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

            entity.HasIndex(e => new
                {
                    e.BatchId,
                    e.DeviceId
                })
                .IsUnique()
                .HasDatabaseName(
                    "ux_telemetry_pings_batch_device");

            entity.HasIndex(e => new
                {
                    e.DeviceId,
                    e.ObservedAtUtc,
                    e.Id
                })
                .IsDescending(false, true, true)
                .HasDatabaseName(
                    "ix_telemetry_pings_device_observed_id");

            // Retained pings prevent deletion of their batch.
            entity.HasOne<SimulationBatch>()
                .WithMany()
                .HasForeignKey(e => e.BatchId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

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

            entity.HasIndex(e => new
                {
                    e.DeviceId,
                    e.OpenedAtUtc,
                    e.Id
                })
                .IsDescending(false, true, true)
                .HasDatabaseName(
                    "ix_incident_logs_device_opened_id");

            // One open incident per device and kind.
            entity.HasIndex(e => new
                {
                    e.DeviceId,
                    e.Kind
                })
                .IsUnique()
                .HasFilter("resolved_at_utc IS NULL")
                .HasDatabaseName(
                    "ux_incident_logs_open_device_kind");

            // Ping history can be pruned without
            // deleting the corresponding incidents.
            entity.HasOne<TelemetryPing>()
                .WithMany()
                .HasForeignKey(e => e.OpenedByPingId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne<TelemetryPing>()
                .WithMany()
                .HasForeignKey(e => e.ResolvedByPingId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }

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

            // Protect the latest ping from retention pruning.
            entity.HasOne<TelemetryPing>()
                .WithMany()
                .HasForeignKey(e => e.LastPingId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
