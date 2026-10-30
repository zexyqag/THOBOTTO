using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class Expressions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "expression_votes",
                columns: table => new
                {
                    expression_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    up = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_expression_votes", x => new { x.expression_id, x.user_id });
                });

            migrationBuilder.CreateTable(
                name: "expressions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    tag = table.Column<string>(type: "text", nullable: true),
                    creator_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    proposer_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    revived_from_id = table.Column<long>(type: "bigint", nullable: true),
                    image = table.Column<byte[]>(type: "bytea", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    animated = table.Column<bool>(type: "boolean", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    paid = table.Column<double>(type: "double precision", nullable: false),
                    vote_channel_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    vote_message_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    proposed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    vote_ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    discord_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    live_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    uses = table.Column<int>(type: "integer", nullable: false),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_expressions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_expressions_guild_id_kind_state",
                table: "expressions",
                columns: new[] { "guild_id", "kind", "state" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "expression_votes");

            migrationBuilder.DropTable(
                name: "expressions");
        }
    }
}
