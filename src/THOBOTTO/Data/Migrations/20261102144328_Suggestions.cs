using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class Suggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "suggestions",
                table: "group_proposals",
                type: "text",
                nullable: false,
                defaultValue: "all");

            migrationBuilder.AddColumn<string>(
                name: "suggestions",
                table: "events",
                type: "text",
                nullable: false,
                defaultValue: "all");

            migrationBuilder.Sql("UPDATE events SET suggestions = 'none' WHERE starts_at IS NULL AND NOT allow_proposals");

            migrationBuilder.DropColumn(
                name: "allow_proposals",
                table: "events");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "starts_at",
                table: "event_suggestions",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            migrationBuilder.AddColumn<long>(
                name: "game_id",
                table: "event_suggestions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "game_name",
                table: "event_suggestions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "title",
                table: "event_suggestions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "suggestions",
                table: "event_series",
                type: "text",
                nullable: false,
                defaultValue: "all");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "suggestions",
                table: "group_proposals");

            migrationBuilder.DropColumn(
                name: "suggestions",
                table: "events");

            migrationBuilder.DropColumn(
                name: "game_id",
                table: "event_suggestions");

            migrationBuilder.DropColumn(
                name: "game_name",
                table: "event_suggestions");

            migrationBuilder.DropColumn(
                name: "title",
                table: "event_suggestions");

            migrationBuilder.DropColumn(
                name: "suggestions",
                table: "event_series");

            migrationBuilder.AddColumn<bool>(
                name: "allow_proposals",
                table: "events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "starts_at",
                table: "event_suggestions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }
    }
}
