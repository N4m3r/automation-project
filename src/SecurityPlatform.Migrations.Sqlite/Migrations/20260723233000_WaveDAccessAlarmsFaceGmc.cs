using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecurityPlatform.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class WaveDAccessAlarmsFaceGmc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Direction",
                table: "AccessDoors",
                type: "TEXT",
                nullable: false,
                defaultValue: "both");

            migrationBuilder.AddColumn<string>(
                name: "ZoneFrom",
                table: "AccessDoors",
                type: "TEXT",
                nullable: false,
                defaultValue: "outside");

            migrationBuilder.AddColumn<string>(
                name: "ZoneTo",
                table: "AccessDoors",
                type: "TEXT",
                nullable: false,
                defaultValue: "inside");

            migrationBuilder.AddColumn<int>(
                name: "InterlockWithDoorId",
                table: "AccessDoors",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "InterlockRequireClosed",
                table: "AccessDoors",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsOpen",
                table: "AccessDoors",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "OpenUntil",
                table: "AccessDoors",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ZoneAfter",
                table: "AccessLogs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "AlarmEvents",
                type: "TEXT",
                nullable: false,
                defaultValue: "open");

            migrationBuilder.AddColumn<int>(
                name: "AssignedUserId",
                table: "AlarmEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TreatmentNotes",
                table: "AlarmEvents",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PopProgressJson",
                table: "AlarmEvents",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<DateTime>(
                name: "ResolvedAt",
                table: "AlarmEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccessPresences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    PersonId = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentZone = table.Column<string>(type: "TEXT", nullable: false),
                    LastDoorId = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessPresences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccessVisitors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    FullName = table.Column<string>(type: "TEXT", nullable: false),
                    HostName = table.Column<string>(type: "TEXT", nullable: false),
                    CredentialValue = table.Column<string>(type: "TEXT", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ValidTo = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessVisitors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FaceGalleryEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ExternalFaceId = table.Column<string>(type: "TEXT", nullable: false),
                    PhotoUrl = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FaceGalleryEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AlarmPopTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    CodePrefix = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    StepsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmPopTemplates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessPresences_TenantId_PersonId",
                table: "AccessPresences",
                columns: new[] { "TenantId", "PersonId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessVisitors_TenantId_ValidTo",
                table: "AccessVisitors",
                columns: new[] { "TenantId", "ValidTo" });

            migrationBuilder.CreateIndex(
                name: "IX_FaceGalleryEntries_TenantId_ExternalFaceId",
                table: "FaceGalleryEntries",
                columns: new[] { "TenantId", "ExternalFaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmPopTemplates_TenantId_CodePrefix",
                table: "AlarmPopTemplates",
                columns: new[] { "TenantId", "CodePrefix" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AccessPresences");
            migrationBuilder.DropTable(name: "AccessVisitors");
            migrationBuilder.DropTable(name: "FaceGalleryEntries");
            migrationBuilder.DropTable(name: "AlarmPopTemplates");

            migrationBuilder.DropColumn(name: "Direction", table: "AccessDoors");
            migrationBuilder.DropColumn(name: "ZoneFrom", table: "AccessDoors");
            migrationBuilder.DropColumn(name: "ZoneTo", table: "AccessDoors");
            migrationBuilder.DropColumn(name: "InterlockWithDoorId", table: "AccessDoors");
            migrationBuilder.DropColumn(name: "InterlockRequireClosed", table: "AccessDoors");
            migrationBuilder.DropColumn(name: "IsOpen", table: "AccessDoors");
            migrationBuilder.DropColumn(name: "OpenUntil", table: "AccessDoors");
            migrationBuilder.DropColumn(name: "ZoneAfter", table: "AccessLogs");
            migrationBuilder.DropColumn(name: "Status", table: "AlarmEvents");
            migrationBuilder.DropColumn(name: "AssignedUserId", table: "AlarmEvents");
            migrationBuilder.DropColumn(name: "TreatmentNotes", table: "AlarmEvents");
            migrationBuilder.DropColumn(name: "PopProgressJson", table: "AlarmEvents");
            migrationBuilder.DropColumn(name: "ResolvedAt", table: "AlarmEvents");
        }
    }
}
