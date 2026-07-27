using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SecurityPlatform.Core.Data;

#nullable disable

namespace SecurityPlatform.Migrations.Postgres.Migrations;

/// <summary>Módulo de busca facial: fingerprint, foto local e lista de interesse.</summary>
[DbContext(typeof(PlatformDbContext))]
[Migration("20260724190000_FaceSearchModule")]
public partial class FaceSearchModule : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PhotoPath",
            table: "FaceGalleryEntries",
            type: "text",
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "EmbeddingJson",
            table: "FaceGalleryEntries",
            type: "text",
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "ListType",
            table: "FaceGalleryEntries",
            type: "text",
            nullable: false,
            defaultValue: "watch");

        migrationBuilder.AddColumn<DateTime>(
            name: "UpdatedAt",
            table: "FaceGalleryEntries",
            type: "timestamp with time zone",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "PhotoPath", table: "FaceGalleryEntries");
        migrationBuilder.DropColumn(name: "EmbeddingJson", table: "FaceGalleryEntries");
        migrationBuilder.DropColumn(name: "ListType", table: "FaceGalleryEntries");
        migrationBuilder.DropColumn(name: "UpdatedAt", table: "FaceGalleryEntries");
    }
}
