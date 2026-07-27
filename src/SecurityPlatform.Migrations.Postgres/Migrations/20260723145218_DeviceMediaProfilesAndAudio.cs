using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecurityPlatform.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class DeviceMediaProfilesAndAudio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LiveProfileId",
                table: "Devices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RecordAudio",
                table: "Devices",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "RecordingProfileId",
                table: "Devices",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LiveProfileId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "RecordAudio",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "RecordingProfileId",
                table: "Devices");
        }
    }
}
