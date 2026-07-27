using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SecurityPlatform.Core.Data;

#nullable disable

namespace SecurityPlatform.Migrations.Sqlite.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("20260724180000_EventActionButtons")]
public partial class EventActionButtons : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "AcknowledgedByUserId",
            table: "Events",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "AcknowledgedAt",
            table: "Events",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "TreatmentStatus",
            table: "Events",
            type: "TEXT",
            nullable: false,
            defaultValue: "open");

        migrationBuilder.AddColumn<string>(
            name: "TreatmentNote",
            table: "Events",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "EventActionButtons",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                Description = table.Column<string>(type: "TEXT", nullable: false),
                Icon = table.Column<string>(type: "TEXT", nullable: false),
                Color = table.Column<string>(type: "TEXT", nullable: false),
                EventTypes = table.Column<string>(type: "TEXT", nullable: false),
                MinSeverity = table.Column<int>(type: "INTEGER", nullable: false),
                Actions = table.Column<string>(type: "TEXT", nullable: false),
                AutoAcknowledge = table.Column<bool>(type: "INTEGER", nullable: false),
                SetStatus = table.Column<string>(type: "TEXT", nullable: true),
                RequireConfirm = table.Column<bool>(type: "INTEGER", nullable: false),
                RequireComment = table.Column<bool>(type: "INTEGER", nullable: false),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_EventActionButtons", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_EventActionButtons_TenantId_SortOrder",
            table: "EventActionButtons",
            columns: new[] { "TenantId", "SortOrder" });

        var now = DateTime.UtcNow.ToString("o");
        migrationBuilder.Sql(
            "INSERT INTO EventActionButtons " +
            "(TenantId, Name, Description, Icon, Color, EventTypes, MinSeverity, Actions, " +
            "AutoAcknowledge, SetStatus, RequireConfirm, RequireComment, SortOrder, Enabled, UpdatedAt) VALUES " +
            $"(1, 'Confirmar', 'Reconhece o evento (ack)', '✓', '#238636', '*', 1, '[]', 1, 'resolved', 0, 0, 10, 1, '{now}'), " +
            $"(1, 'Em tratamento', 'Marca como em atendimento', '🔧', '#1f6feb', '*', 1, '[]', 1, 'treating', 0, 0, 20, 1, '{now}'), " +
            $"(1, 'Resolver', 'Fecha o tratamento com nota opcional', '✔', '#3fb950', '*', 1, '[]', 1, 'resolved', 0, 1, 30, 1, '{now}'), " +
            $"(1, 'Ao vivo', 'Abre a câmera do evento em live', '▶', '#58a6ff', '*', 1, " +
            "'[{\"kind\":\"OpenLive\"}]', 0, NULL, 0, 0, 40, 1, '" + now + "'), " +
            $"(1, 'Playback', 'Abre gravações da câmera do evento', '📼', '#8b949e', '*', 1, " +
            "'[{\"kind\":\"OpenPlayback\"}]', 0, NULL, 0, 0, 50, 1, '" + now + "');");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "EventActionButtons");
        migrationBuilder.DropColumn(name: "AcknowledgedByUserId", table: "Events");
        migrationBuilder.DropColumn(name: "AcknowledgedAt", table: "Events");
        migrationBuilder.DropColumn(name: "TreatmentStatus", table: "Events");
        migrationBuilder.DropColumn(name: "TreatmentNote", table: "Events");
    }
}
