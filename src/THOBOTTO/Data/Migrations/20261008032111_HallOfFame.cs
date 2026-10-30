using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class HallOfFame : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fame_entries",
                columns: table => new
                {
                    message_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    channel_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    author_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    reactors = table.Column<int>(type: "integer", nullable: false),
                    showcase_message_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    inducted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fame_entries", x => x.message_id);
                });

            migrationBuilder.CreateTable(
                name: "fame_reactions",
                columns: table => new
                {
                    message_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    reactor_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    emoji = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fame_reactions", x => new { x.message_id, x.reactor_id, x.emoji });
                });

            migrationBuilder.CreateIndex(
                name: "ix_fame_entries_guild_id_inducted_at",
                table: "fame_entries",
                columns: new[] { "guild_id", "inducted_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fame_entries");

            migrationBuilder.DropTable(
                name: "fame_reactions");
        }
    }
}
