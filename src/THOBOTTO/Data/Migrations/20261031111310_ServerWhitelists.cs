using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class ServerWhitelists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "whitelist_for",
                table: "game_servers",
                type: "text",
                nullable: false,
                defaultValue: "off");

            migrationBuilder.AddColumn<decimal>(
                name: "whitelist_role_id",
                table: "game_servers",
                type: "numeric(20,0)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "whitelist_entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    server_id = table.Column<long>(type: "bigint", nullable: false),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    removing = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_whitelist_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_whitelist_entries_game_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "game_servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_whitelist_entries_guild_id_user_id",
                table: "whitelist_entries",
                columns: new[] { "guild_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_whitelist_entries_server_id_user_id",
                table: "whitelist_entries",
                columns: new[] { "server_id", "user_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "whitelist_entries");

            migrationBuilder.DropColumn(
                name: "whitelist_for",
                table: "game_servers");

            migrationBuilder.DropColumn(
                name: "whitelist_role_id",
                table: "game_servers");
        }
    }
}
