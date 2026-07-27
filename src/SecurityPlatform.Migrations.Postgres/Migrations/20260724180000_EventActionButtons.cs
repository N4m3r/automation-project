using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SecurityPlatform.Core.Data;

#nullable disable

namespace SecurityPlatform.Migrations.Postgres.Migrations;

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
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "AcknowledgedAt",
            table: "Events",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "TreatmentStatus",
            table: "Events",
            type: "text",
            nullable: false,
            defaultValue: "open");

        migrationBuilder.AddColumn<string>(
            name: "TreatmentNote",
            table: "Events",
            type: "text",
            nullable: true);

        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS "EventActionButtons" (
                "Id" serial PRIMARY KEY,
                "TenantId" integer NOT NULL,
                "Name" text NOT NULL,
                "Description" text NOT NULL,
                "Icon" text NOT NULL,
                "Color" text NOT NULL,
                "EventTypes" text NOT NULL,
                "MinSeverity" integer NOT NULL,
                "Actions" text NOT NULL,
                "AutoAcknowledge" boolean NOT NULL,
                "SetStatus" text NULL,
                "RequireConfirm" boolean NOT NULL,
                "RequireComment" boolean NOT NULL,
                "SortOrder" integer NOT NULL,
                "Enabled" boolean NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_EventActionButtons_TenantId_SortOrder"
                ON "EventActionButtons" ("TenantId", "SortOrder");

            INSERT INTO "EventActionButtons"
            ("TenantId", "Name", "Description", "Icon", "Color", "EventTypes", "MinSeverity", "Actions",
             "AutoAcknowledge", "SetStatus", "RequireConfirm", "RequireComment", "SortOrder", "Enabled", "UpdatedAt")
            SELECT * FROM (VALUES
            (1, 'Confirmar', 'Reconhece o evento (ack)', '✓', '#238636', '*', 1, '[]',
             TRUE, 'resolved', FALSE, FALSE, 10, TRUE, NOW() AT TIME ZONE 'UTC'),
            (1, 'Em tratamento', 'Marca como em atendimento', '🔧', '#1f6feb', '*', 1, '[]',
             TRUE, 'treating', FALSE, FALSE, 20, TRUE, NOW() AT TIME ZONE 'UTC'),
            (1, 'Resolver', 'Fecha o tratamento com nota opcional', '✔', '#3fb950', '*', 1, '[]',
             TRUE, 'resolved', FALSE, TRUE, 30, TRUE, NOW() AT TIME ZONE 'UTC'),
            (1, 'Ao vivo', 'Abre a câmera do evento em live', '▶', '#58a6ff', '*', 1,
             '[{"kind":"OpenLive"}]', FALSE, NULL::text, FALSE, FALSE, 40, TRUE, NOW() AT TIME ZONE 'UTC'),
            (1, 'Playback', 'Abre gravações da câmera do evento', '📼', '#8b949e', '*', 1,
             '[{"kind":"OpenPlayback"}]', FALSE, NULL::text, FALSE, FALSE, 50, TRUE, NOW() AT TIME ZONE 'UTC')
            ) AS v
            WHERE NOT EXISTS (SELECT 1 FROM "EventActionButtons" LIMIT 1);
            """);
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
