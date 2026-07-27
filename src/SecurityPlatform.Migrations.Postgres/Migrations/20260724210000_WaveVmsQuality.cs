using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using SecurityPlatform.Core.Data;

#nullable disable

namespace SecurityPlatform.Migrations.Postgres.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("20260724210000_WaveVmsQuality")]
public partial class WaveVmsQuality : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "PreEventSeconds",
            table: "Devices",
            type: "integer",
            nullable: false,
            defaultValue: 15);

        migrationBuilder.AddColumn<string>(
            name: "ArchivePath",
            table: "SystemSettings",
            type: "text",
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<int>(
            name: "ArchiveAfterDays",
            table: "SystemSettings",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.CreateTable(
            name: "ExportRecords",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                TenantId = table.Column<int>(type: "integer", nullable: false),
                DeviceId = table.Column<int>(type: "integer", nullable: false),
                UserId = table.Column<int>(type: "integer", nullable: true),
                UserName = table.Column<string>(type: "text", nullable: false),
                FromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ToUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                FileName = table.Column<string>(type: "text", nullable: false),
                FilePath = table.Column<string>(type: "text", nullable: true),
                SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                Sha256 = table.Column<string>(type: "text", nullable: false),
                Signature = table.Column<string>(type: "text", nullable: true),
                Watermark = table.Column<bool>(type: "boolean", nullable: false),
                BlurFaces = table.Column<bool>(type: "boolean", nullable: false),
                SegmentCount = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ExportRecords", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_ExportRecords_TenantId_CreatedAt",
            table: "ExportRecords",
            columns: new[] { "TenantId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_ExportRecords_Sha256",
            table: "ExportRecords",
            column: "Sha256");

        migrationBuilder.CreateTable(
            name: "RetentionPurgeLogs",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                TenantId = table.Column<int>(type: "integer", nullable: false),
                DeviceId = table.Column<int>(type: "integer", nullable: false),
                RecordingId = table.Column<long>(type: "bigint", nullable: true),
                Path = table.Column<string>(type: "text", nullable: false),
                SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                Reason = table.Column<string>(type: "text", nullable: false),
                PurgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_RetentionPurgeLogs", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_RetentionPurgeLogs_TenantId_PurgedAt",
            table: "RetentionPurgeLogs",
            columns: new[] { "TenantId", "PurgedAt" });

        migrationBuilder.CreateTable(
            name: "PrivacyMasks",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                TenantId = table.Column<int>(type: "integer", nullable: false),
                DeviceId = table.Column<int>(type: "integer", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                PolygonsJson = table.Column<string>(type: "text", nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_PrivacyMasks", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_PrivacyMasks_DeviceId_Enabled",
            table: "PrivacyMasks",
            columns: new[] { "DeviceId", "Enabled" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ExportRecords");
        migrationBuilder.DropTable(name: "RetentionPurgeLogs");
        migrationBuilder.DropTable(name: "PrivacyMasks");
        migrationBuilder.DropColumn(name: "PreEventSeconds", table: "Devices");
        migrationBuilder.DropColumn(name: "ArchivePath", table: "SystemSettings");
        migrationBuilder.DropColumn(name: "ArchiveAfterDays", table: "SystemSettings");
    }
}
