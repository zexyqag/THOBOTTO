using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class Polls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "poll_votes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    poll_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    option = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_poll_votes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "polls",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    channel_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    message_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    asker_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    question = table.Column<string>(type: "text", nullable: false),
                    options = table.Column<string[]>(type: "text[]", nullable: false),
                    in_voice = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closes_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_polls", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_poll_votes_poll_id_user_id",
                table: "poll_votes",
                columns: new[] { "poll_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_polls_guild_id_closed",
                table: "polls",
                columns: new[] { "guild_id", "closed" });

            // Polls in the Activity worked before there was a module for them: on where the bot is set up.
            migrationBuilder.Sql("""
                INSERT INTO enabled_modules (guild_id, module)
                SELECT DISTINCT guild_id, 'polls' FROM enabled_modules e
                WHERE NOT EXISTS (SELECT 1 FROM enabled_modules p WHERE p.guild_id = e.guild_id AND p.module = 'polls');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "poll_votes");

            migrationBuilder.DropTable(
                name: "polls");
        }
    }
}
