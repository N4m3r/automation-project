using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecurityPlatform.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class RecordingEncryptedAndMonitorLayout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Encrypted",
                table: "Recordings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "MonitorLayouts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    LayoutId = table.Column<string>(type: "TEXT", nullable: false),
                    CellsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitorLayouts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MonitorLayouts_UserId_Name",
                table: "MonitorLayouts",
                columns: new[] { "UserId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MonitorLayouts");

            migrationBuilder.DropColumn(
                name: "Encrypted",
                table: "Recordings");
        }
    }
}
