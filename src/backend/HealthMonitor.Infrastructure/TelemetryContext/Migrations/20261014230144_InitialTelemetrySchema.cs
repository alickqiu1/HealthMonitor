using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HealthMonitor.Infrastructure.TelemetryContext.Migrations
{
    /// <inheritdoc />
    public partial class InitialTelemetrySchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "telemetry");

            migrationBuilder.CreateTable(
                name: "simulation_batches",
                schema: "telemetry",
                columns: table => new
                {
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    scenario = table.Column<string>(type: "text", nullable: false),
                    requested_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    committed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    affected_device_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_simulation_batches", x => x.batch_id);
                    table.CheckConstraint("ck_simulation_batches_affected_count", "affected_device_count >= 0");
                    table.CheckConstraint("ck_simulation_batches_commit_time", "committed_at_utc >= requested_at_utc");
                });

            migrationBuilder.CreateTable(
                name: "telemetry_pings",
                schema: "telemetry",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    observed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    latency_ms = table.Column<int>(type: "integer", nullable: true),
                    cpu_percent = table.Column<decimal>(type: "numeric(5,2)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_telemetry_pings", x => x.id);
                    table.CheckConstraint("ck_telemetry_pings_measurements", "(\r\n                            outcome = 'Timeout'\r\n                            AND latency_ms IS NULL\r\n                            AND cpu_percent IS NULL\r\n                          )\r\n                          OR\r\n                          (\r\n                            outcome = 'Success'\r\n                            AND latency_ms IS NOT NULL\r\n                            AND cpu_percent IS NOT NULL\r\n                            AND latency_ms BETWEEN 0 AND 60000\r\n                            AND cpu_percent BETWEEN 0 AND 100\r\n                          )");
                    table.CheckConstraint("ck_telemetry_pings_outcome", "outcome IN ('Success', 'Timeout')");
                    table.ForeignKey(
                        name: "FK_telemetry_pings_simulation_batches_batch_id",
                        column: x => x.batch_id,
                        principalSchema: "telemetry",
                        principalTable: "simulation_batches",
                        principalColumn: "batch_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "device_telemetry_state",
                schema: "telemetry",
                columns: table => new
                {
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_ping_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_observed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_successful_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    latest_outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    latest_latency_ms = table.Column<int>(type: "integer", nullable: true),
                    latest_cpu_percent = table.Column<decimal>(type: "numeric(5,2)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_telemetry_state", x => x.device_id);
                    table.CheckConstraint("ck_device_state_measurements", "(\r\n                            latest_outcome = 'Timeout'\r\n                            AND latest_latency_ms IS NULL\r\n                            AND latest_cpu_percent IS NULL\r\n                          )\r\n                          OR\r\n                          (\r\n                            latest_outcome = 'Success'\r\n                            AND latest_latency_ms IS NOT NULL\r\n                            AND latest_cpu_percent IS NOT NULL\r\n                            AND latest_latency_ms BETWEEN 0 AND 60000\r\n                            AND latest_cpu_percent BETWEEN 0 AND 100\r\n                          )");
                    table.CheckConstraint("ck_device_state_outcome", "latest_outcome IN ('Success', 'Timeout')");
                    table.CheckConstraint("ck_device_state_success_time", "last_successful_at_utc IS NULL OR last_successful_at_utc <= last_observed_at_utc");
                    table.ForeignKey(
                        name: "FK_device_telemetry_state_telemetry_pings_last_ping_id",
                        column: x => x.last_ping_id,
                        principalSchema: "telemetry",
                        principalTable: "telemetry_pings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "incident_logs",
                schema: "telemetry",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    opened_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    opened_by_ping_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_by_ping_id = table.Column<Guid>(type: "uuid", nullable: true),
                    summary = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incident_logs", x => x.id);
                    table.CheckConstraint("ck_incident_logs_kind", "kind IN ('Latency', 'Cpu', 'Connectivity')");
                    table.CheckConstraint("ck_incident_logs_resolution_time", "resolved_at_utc IS NULL OR resolved_at_utc >= opened_at_utc");
                    table.ForeignKey(
                        name: "FK_incident_logs_telemetry_pings_opened_by_ping_id",
                        column: x => x.opened_by_ping_id,
                        principalSchema: "telemetry",
                        principalTable: "telemetry_pings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_incident_logs_telemetry_pings_resolved_by_ping_id",
                        column: x => x.resolved_by_ping_id,
                        principalSchema: "telemetry",
                        principalTable: "telemetry_pings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_device_telemetry_state_last_ping_id",
                schema: "telemetry",
                table: "device_telemetry_state",
                column: "last_ping_id");

            migrationBuilder.CreateIndex(
                name: "ix_incident_logs_device_opened_id",
                schema: "telemetry",
                table: "incident_logs",
                columns: new[] { "device_id", "opened_at_utc", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_incident_logs_opened_by_ping_id",
                schema: "telemetry",
                table: "incident_logs",
                column: "opened_by_ping_id");

            migrationBuilder.CreateIndex(
                name: "IX_incident_logs_resolved_by_ping_id",
                schema: "telemetry",
                table: "incident_logs",
                column: "resolved_by_ping_id");

            migrationBuilder.CreateIndex(
                name: "ux_incident_logs_open_device_kind",
                schema: "telemetry",
                table: "incident_logs",
                columns: new[] { "device_id", "kind" },
                unique: true,
                filter: "resolved_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_pings_device_observed_id",
                schema: "telemetry",
                table: "telemetry_pings",
                columns: new[] { "device_id", "observed_at_utc", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ux_telemetry_pings_batch_device",
                schema: "telemetry",
                table: "telemetry_pings",
                columns: new[] { "batch_id", "device_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_telemetry_state",
                schema: "telemetry");

            migrationBuilder.DropTable(
                name: "incident_logs",
                schema: "telemetry");

            migrationBuilder.DropTable(
                name: "telemetry_pings",
                schema: "telemetry");

            migrationBuilder.DropTable(
                name: "simulation_batches",
                schema: "telemetry");
        }
    }
}
