using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class EventLengthsAndCalendars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "length_minutes",
                table: "events",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "length_minutes",
                table: "event_series",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "calendar_links",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    token = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    choices = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calendar_links", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_calendar_links_guild_id",
                table: "calendar_links",
                column: "guild_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_calendar_links_token",
                table: "calendar_links",
                column: "token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_calendar_links_user_id",
                table: "calendar_links",
                column: "user_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calendar_links");

            migrationBuilder.DropColumn(
                name: "length_minutes",
                table: "events");

            migrationBuilder.DropColumn(
                name: "length_minutes",
                table: "event_series");
        }
    }
}
