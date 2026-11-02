using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class GroupGameVotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long[]>(
                name: "game_ids",
                table: "group_proposals",
                type: "bigint[]",
                nullable: false,
                defaultValue: new long[0]);

            migrationBuilder.AlterColumn<string>(
                name: "title",
                table: "group_proposals",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.Sql("UPDATE group_proposals SET game_ids = ARRAY[game_id]");
            migrationBuilder.Sql("UPDATE group_proposals p SET title = NULL FROM games g WHERE g.id = p.game_id AND p.title = g.name || ' session'");

            migrationBuilder.DropForeignKey(
                name: "fk_group_proposals_games_game_id",
                table: "group_proposals");

            migrationBuilder.DropIndex(
                name: "ix_group_proposals_game_id",
                table: "group_proposals");

            migrationBuilder.DropColumn(
                name: "game_id",
                table: "group_proposals");

            migrationBuilder.CreateTable(
                name: "group_lineups",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    starter_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    shown = table.Column<string[]>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_lineups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "group_proposal_game_votes",
                columns: table => new
                {
                    proposal_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    game_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_proposal_game_votes", x => new { x.proposal_id, x.user_id, x.game_id });
                    table.ForeignKey(
                        name: "fk_group_proposal_game_votes_group_proposals_proposal_id",
                        column: x => x.proposal_id,
                        principalTable: "group_proposals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "group_lineup_members",
                columns: table => new
                {
                    lineup_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_lineup_members", x => new { x.lineup_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_group_lineup_members_group_lineups_lineup_id",
                        column: x => x.lineup_id,
                        principalTable: "group_lineups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "group_lineup_members");

            migrationBuilder.DropTable(
                name: "group_proposal_game_votes");

            migrationBuilder.DropTable(
                name: "group_lineups");

            migrationBuilder.AddColumn<long>(
                name: "game_id",
                table: "group_proposals",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.Sql("DELETE FROM group_proposals WHERE cardinality(game_ids) = 0");
            migrationBuilder.Sql("UPDATE group_proposals SET game_id = game_ids[1]");
            migrationBuilder.Sql("UPDATE group_proposals p SET title = g.name || ' session' FROM games g WHERE g.id = p.game_id AND p.title IS NULL");
            migrationBuilder.Sql("DELETE FROM group_proposals WHERE title IS NULL");

            migrationBuilder.DropColumn(
                name: "game_ids",
                table: "group_proposals");

            migrationBuilder.AlterColumn<string>(
                name: "title",
                table: "group_proposals",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_group_proposals_game_id",
                table: "group_proposals",
                column: "game_id");

            migrationBuilder.AddForeignKey(
                name: "fk_group_proposals_games_game_id",
                table: "group_proposals",
                column: "game_id",
                principalTable: "games",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
