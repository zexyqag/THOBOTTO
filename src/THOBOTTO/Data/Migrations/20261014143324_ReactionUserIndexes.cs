using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReactionUserIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_archived_reactions_user_id",
                table: "archived_reactions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_archived_poll_votes_user_id",
                table: "archived_poll_votes",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_archived_reactions_user_id",
                table: "archived_reactions");

            migrationBuilder.DropIndex(
                name: "ix_archived_poll_votes_user_id",
                table: "archived_poll_votes");
        }
    }
}
