using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class Backfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "backfill_channels",
                columns: table => new
                {
                    channel_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    cursor = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    fetched = table.Column<int>(type: "integer", nullable: false),
                    done = table.Column<bool>(type: "boolean", nullable: false),
                    problem = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backfill_channels", x => x.channel_id);
                });

            migrationBuilder.CreateTable(
                name: "backfill_runs",
                columns: table => new
                {
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    running = table.Column<bool>(type: "boolean", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backfill_runs", x => x.guild_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_backfill_channels_guild_id",
                table: "backfill_channels",
                column: "guild_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backfill_channels");

            migrationBuilder.DropTable(
                name: "backfill_runs");
        }
    }
}
