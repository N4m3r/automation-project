using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SecurityPlatform.Core.Data;

#nullable disable

namespace SecurityPlatform.Migrations.Postgres.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("20260724200000_AutomationSchedule")]
public partial class AutomationSchedule : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ScheduleDays",
            table: "AutomationRules",
            type: "text",
            nullable: false,
            defaultValue: "0,1,2,3,4,5,6");

        migrationBuilder.AddColumn<string>(
            name: "ScheduleStart",
            table: "AutomationRules",
            type: "text",
            nullable: false,
            defaultValue: "00:00");

        migrationBuilder.AddColumn<string>(
            name: "ScheduleEnd",
            table: "AutomationRules",
            type: "text",
            nullable: false,
            defaultValue: "23:59");

        migrationBuilder.AddColumn<string>(
            name: "TimeZone",
            table: "AutomationRules",
            type: "text",
            nullable: false,
            defaultValue: "America/Sao_Paulo");

        migrationBuilder.AddColumn<int>(
            name: "CooldownSeconds",
            table: "AutomationRules",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTime>(
            name: "LastFiredAt",
            table: "AutomationRules",
            type: "timestamp with time zone",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ScheduleDays", table: "AutomationRules");
        migrationBuilder.DropColumn(name: "ScheduleStart", table: "AutomationRules");
        migrationBuilder.DropColumn(name: "ScheduleEnd", table: "AutomationRules");
        migrationBuilder.DropColumn(name: "TimeZone", table: "AutomationRules");
        migrationBuilder.DropColumn(name: "CooldownSeconds", table: "AutomationRules");
        migrationBuilder.DropColumn(name: "LastFiredAt", table: "AutomationRules");
    }
}
