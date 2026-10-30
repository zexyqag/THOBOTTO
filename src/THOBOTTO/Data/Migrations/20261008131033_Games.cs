using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class Games : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "game_id",
                table: "events",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "game_pickers",
                columns: table => new
                {
                    message_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    channel_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_game_pickers", x => x.message_id);
                });

            migrationBuilder.CreateTable(
                name: "games",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    emoji = table.Column<string>(type: "text", nullable: true),
                    role_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    server_id = table.Column<long>(type: "bigint", nullable: true),
                    channel_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_games", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_games_guild_id_role_id",
                table: "games",
                columns: new[] { "guild_id", "role_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "game_pickers");

            migrationBuilder.DropTable(
                name: "games");

            migrationBuilder.DropColumn(
                name: "game_id",
                table: "events");
        }
    }
}
