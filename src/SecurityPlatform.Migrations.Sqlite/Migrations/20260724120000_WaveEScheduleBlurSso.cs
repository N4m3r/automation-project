using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SecurityPlatform.Core.Data;

#nullable disable

namespace SecurityPlatform.Migrations.Sqlite.Migrations;

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
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<int>(
            name: "ScheduleId",
            table: "AccessDoors",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "ScheduleId",
            table: "AccessPeople",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "AccessSchedules",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                DaysOfWeek = table.Column<string>(type: "TEXT", nullable: false),
                StartHm = table.Column<string>(type: "TEXT", nullable: false),
                EndHm = table.Column<string>(type: "TEXT", nullable: false),
                TimeZone = table.Column<string>(type: "TEXT", nullable: false),
                Active = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
