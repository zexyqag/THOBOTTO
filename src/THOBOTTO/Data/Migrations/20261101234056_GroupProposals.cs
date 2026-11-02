using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class GroupProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "group_proposals",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    game_id = table.Column<long>(type: "bigint", nullable: false),
                    proposer_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    proposer_name = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    times = table.Column<DateTimeOffset[]>(type: "timestamp with time zone[]", nullable: false),
                    event_id = table.Column<long>(type: "bigint", nullable: true),
                    planned = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_proposals", x => x.id);
                    table.ForeignKey(
                        name: "fk_group_proposals_games_game_id",
                        column: x => x.game_id,
                        principalTable: "games",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "group_proposal_votes",
                columns: table => new
                {
                    proposal_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    choice = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_proposal_votes", x => new { x.proposal_id, x.user_id, x.choice });
                    table.ForeignKey(
                        name: "fk_group_proposal_votes_group_proposals_proposal_id",
                        column: x => x.proposal_id,
                        principalTable: "group_proposals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_group_proposals_game_id",
                table: "group_proposals",
                column: "game_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "group_proposal_votes");

            migrationBuilder.DropTable(
                name: "group_proposals");
        }
    }
}
