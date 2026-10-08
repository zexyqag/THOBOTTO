using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class Points : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "point_accounts",
                columns: table => new
                {
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    balance = table.Column<double>(type: "double precision", nullable: false),
                    voice = table.Column<double>(type: "double precision", nullable: false),
                    chat = table.Column<double>(type: "double precision", nullable: false),
                    received = table.Column<double>(type: "double precision", nullable: false),
                    given = table.Column<double>(type: "double precision", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_chat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    pending_earned = table.Column<double>(type: "double precision", nullable: false),
                    pending_expired = table.Column<double>(type: "double precision", nullable: false),
                    pending_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_point_accounts", x => new { x.guild_id, x.user_id });
                });

            migrationBuilder.CreateTable(
                name: "point_entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    amount = table.Column<double>(type: "double precision", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    actor_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_point_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "point_settings",
                columns: table => new
                {
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    rules = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_point_settings", x => x.guild_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_point_entries_guild_id_user_id_created_at",
                table: "point_entries",
                columns: new[] { "guild_id", "user_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "point_accounts");

            migrationBuilder.DropTable(
                name: "point_entries");

            migrationBuilder.DropTable(
                name: "point_settings");
        }
    }
}
