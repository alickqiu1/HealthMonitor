using Microsoft.EntityFrameworkCore;
using HealthMonitor.Domain.Device;

namespace HealthMonitor.Infrastructure.Data.DeviceContext;

/// <summary>
/// EF Core context owning the Device inventory schema.
/// </summary>
public sealed class DeviceDbContext : DbContext
{
    public DbSet<MonitoredDevice> Devices => Set<MonitoredDevice>();

    public DeviceDbContext(
        DbContextOptions<DeviceDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("device");

        modelBuilder.Entity<MonitoredDevice>(entity =>
        {
            entity.ToTable("monitored_devices", "device");

            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();

            entity.Property(e => e.AssetTag)
                .HasColumnName("asset_tag")
                .HasMaxLength(64)
                .IsRequired();

            entity.Property(e => e.DisplayName)
                .HasColumnName("display_name")
                .HasMaxLength(120)
                .IsRequired();

            entity.Property(e => e.DeviceType)
                .HasColumnName("device_type")
                .HasMaxLength(32)
                .IsRequired();

            entity.Property(e => e.SiteCode)
                .HasColumnName("site_code")
                .HasMaxLength(32)
                .IsRequired();

            entity.Property(e => e.CreatedAtUtc)
                .HasColumnName("created_at_utc")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.Property(e => e.RetiredAtUtc)
                .HasColumnName("retired_at_utc")
                .HasColumnType("timestamp with time zone")
                .IsRequired(false);

            // Asset tags are unique across the catalog.
            entity.HasIndex(e => e.AssetTag)
                .IsUnique()
                .HasDatabaseName("ux_monitored_devices_asset_tag");

            // Supports site filtering with deterministic ID ordering.
            entity.HasIndex(e => new { e.SiteCode, e.Id })
                .HasDatabaseName("ix_monitored_devices_site_code_id");
        });
    }
}
