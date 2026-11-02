using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlanRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "closed",
                table: "group_proposals",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "closes_at",
                table: "group_proposals",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "decide",
                table: "group_proposals",
                type: "text",
                nullable: false,
                defaultValue: "planner");

            migrationBuilder.AddColumn<int>(
                name: "enough",
                table: "group_proposals",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "decide",
                table: "events",
                type: "text",
                nullable: false,
                defaultValue: "closes");

            migrationBuilder.AddColumn<int>(
                name: "enough",
                table: "events",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql("UPDATE group_proposals SET closes_at = created_at + interval '24 hours'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "closed",
                table: "group_proposals");

            migrationBuilder.DropColumn(
                name: "closes_at",
                table: "group_proposals");

            migrationBuilder.DropColumn(
                name: "decide",
                table: "group_proposals");

            migrationBuilder.DropColumn(
                name: "enough",
                table: "group_proposals");

            migrationBuilder.DropColumn(
                name: "decide",
                table: "events");

            migrationBuilder.DropColumn(
                name: "enough",
                table: "events");
        }
    }
}
