using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class EventSeries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "series_id",
                table: "events",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "event_series",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    channel_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    creator_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    ping_role_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    days = table.Column<int[]>(type: "integer[]", nullable: false),
                    time_of_day = table.Column<int>(type: "integer", nullable: false),
                    zone = table.Column<string>(type: "text", nullable: false),
                    open_days_ahead = table.Column<int>(type: "integer", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_series", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_events_series_id_starts_at",
                table: "events",
                columns: new[] { "series_id", "starts_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "event_series");

            migrationBuilder.DropIndex(
                name: "ix_events_series_id_starts_at",
                table: "events");

            migrationBuilder.DropColumn(
                name: "series_id",
                table: "events");
        }
    }
}
