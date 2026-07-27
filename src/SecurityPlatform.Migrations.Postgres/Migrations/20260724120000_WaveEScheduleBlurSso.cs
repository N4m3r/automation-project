using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using SecurityPlatform.Core.Data;

#nullable disable

namespace SecurityPlatform.Migrations.Postgres.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("20260724120000_WaveEScheduleBlurSso")]
public partial class WaveEScheduleBlurSso : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "BlurFacesOnExport",
            table: "SystemSettings",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<int>(
            name: "ScheduleId",
            table: "AccessDoors",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "ScheduleId",
            table: "AccessPeople",
            type: "integer",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "AccessSchedules",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                TenantId = table.Column<int>(type: "integer", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                DaysOfWeek = table.Column<string>(type: "text", nullable: false),
                StartHm = table.Column<string>(type: "text", nullable: false),
                EndHm = table.Column<string>(type: "text", nullable: false),
                TimeZone = table.Column<string>(type: "text", nullable: false),
                Active = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_AccessSchedules", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_AccessSchedules_TenantId_Name",
            table: "AccessSchedules",
            columns: new[] { "TenantId", "Name" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "AccessSchedules");
        migrationBuilder.DropColumn(name: "BlurFacesOnExport", table: "SystemSettings");
        migrationBuilder.DropColumn(name: "ScheduleId", table: "AccessDoors");
        migrationBuilder.DropColumn(name: "ScheduleId", table: "AccessPeople");
    }
}
