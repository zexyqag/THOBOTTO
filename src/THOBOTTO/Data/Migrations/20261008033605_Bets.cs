using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class Bets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bet_payouts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    bet_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    amount = table.Column<double>(type: "double precision", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    reversed = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bet_payouts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bet_stakes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    bet_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    option = table.Column<int>(type: "integer", nullable: false),
                    amount = table.Column<double>(type: "double precision", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bet_stakes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bets",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    channel_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    message_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    creator_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    question = table.Column<string>(type: "text", nullable: false),
                    options = table.Column<string[]>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closes_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    winning_option = table.Column<int>(type: "integer", nullable: true),
                    resolved_by_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_shown = table.Column<bool>(type: "boolean", nullable: false),
                    creator_cut_percent = table.Column<double>(type: "double precision", nullable: false),
                    house_cut_percent = table.Column<double>(type: "double precision", nullable: false),
                    creator_can_bet = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bets", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_bet_payouts_bet_id",
                table: "bet_payouts",
                column: "bet_id");

            migrationBuilder.CreateIndex(
                name: "ix_bet_stakes_bet_id_user_id",
                table: "bet_stakes",
                columns: new[] { "bet_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_bets_guild_id_state",
                table: "bets",
                columns: new[] { "guild_id", "state" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bet_payouts");

            migrationBuilder.DropTable(
                name: "bet_stakes");

            migrationBuilder.DropTable(
                name: "bets");
        }
    }
}
