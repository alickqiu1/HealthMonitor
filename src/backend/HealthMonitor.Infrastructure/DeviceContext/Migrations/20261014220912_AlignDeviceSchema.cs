using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HealthMonitor.Infrastructure.DeviceContext.Migrations
{
    /// <inheritdoc />
    public partial class AlignDeviceSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_Devices",
                schema: "device",
                table: "Devices");

            migrationBuilder.DropIndex(
                name: "IX_Devices_SiteCode",
                schema: "device",
                table: "Devices");

            migrationBuilder.RenameTable(
                name: "Devices",
                schema: "device",
                newName: "monitored_devices",
                newSchema: "device");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "device",
                table: "monitored_devices",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "SiteCode",
                schema: "device",
                table: "monitored_devices",
                newName: "site_code");

            migrationBuilder.RenameColumn(
                name: "RetiredAtUtc",
                schema: "device",
                table: "monitored_devices",
                newName: "retired_at_utc");

            migrationBuilder.RenameColumn(
                name: "DisplayName",
                schema: "device",
                table: "monitored_devices",
                newName: "display_name");

            migrationBuilder.RenameColumn(
                name: "DeviceType",
                schema: "device",
                table: "monitored_devices",
                newName: "device_type");

            migrationBuilder.RenameColumn(
                name: "CreatedAtUtc",
                schema: "device",
                table: "monitored_devices",
                newName: "created_at_utc");

            migrationBuilder.RenameColumn(
                name: "AssetTag",
                schema: "device",
                table: "monitored_devices",
                newName: "asset_tag");

            migrationBuilder.RenameIndex(
                name: "IX_Devices_AssetTag",
                schema: "device",
                table: "monitored_devices",
                newName: "ux_monitored_devices_asset_tag");

            migrationBuilder.AlterColumn<string>(
                name: "site_code",
                schema: "device",
                table: "monitored_devices",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10);

            migrationBuilder.AlterColumn<string>(
                name: "display_name",
                schema: "device",
                table: "monitored_devices",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.AlterColumn<string>(
                name: "device_type",
                schema: "device",
                table: "monitored_devices",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.AlterColumn<string>(
                name: "asset_tag",
                schema: "device",
                table: "monitored_devices",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.AddPrimaryKey(
                name: "PK_monitored_devices",
                schema: "device",
                table: "monitored_devices",
                column: "id");

            migrationBuilder.CreateIndex(
                name: "ix_monitored_devices_site_code_id",
                schema: "device",
                table: "monitored_devices",
                columns: new[] { "site_code", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_monitored_devices",
                schema: "device",
                table: "monitored_devices");

            migrationBuilder.DropIndex(
                name: "ix_monitored_devices_site_code_id",
                schema: "device",
                table: "monitored_devices");

            migrationBuilder.RenameTable(
                name: "monitored_devices",
                schema: "device",
                newName: "Devices",
                newSchema: "device");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "device",
                table: "Devices",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "site_code",
                schema: "device",
                table: "Devices",
                newName: "SiteCode");

            migrationBuilder.RenameColumn(
                name: "retired_at_utc",
                schema: "device",
                table: "Devices",
                newName: "RetiredAtUtc");

            migrationBuilder.RenameColumn(
                name: "display_name",
                schema: "device",
                table: "Devices",
                newName: "DisplayName");

            migrationBuilder.RenameColumn(
                name: "device_type",
                schema: "device",
                table: "Devices",
                newName: "DeviceType");

            migrationBuilder.RenameColumn(
                name: "created_at_utc",
                schema: "device",
                table: "Devices",
                newName: "CreatedAtUtc");

            migrationBuilder.RenameColumn(
                name: "asset_tag",
                schema: "device",
                table: "Devices",
                newName: "AssetTag");

            migrationBuilder.RenameIndex(
                name: "ux_monitored_devices_asset_tag",
                schema: "device",
                table: "Devices",
                newName: "IX_Devices_AssetTag");

            migrationBuilder.AlterColumn<string>(
                name: "SiteCode",
                schema: "device",
                table: "Devices",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<string>(
                name: "DisplayName",
                schema: "device",
                table: "Devices",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(120)",
                oldMaxLength: 120);

            migrationBuilder.AlterColumn<string>(
                name: "DeviceType",
                schema: "device",
                table: "Devices",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<string>(
                name: "AssetTag",
                schema: "device",
                table: "Devices",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AddPrimaryKey(
                name: "PK_Devices",
                schema: "device",
                table: "Devices",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_SiteCode",
                schema: "device",
                table: "Devices",
                column: "SiteCode");
        }
    }
}
