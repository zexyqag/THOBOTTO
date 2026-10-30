using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class HappeningsPerServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_happening_posts_kind_thing_id",
                table: "happening_posts");

            migrationBuilder.CreateIndex(
                name: "ix_happening_posts_guild_id_kind_thing_id",
                table: "happening_posts",
                columns: new[] { "guild_id", "kind", "thing_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_happening_posts_guild_id_kind_thing_id",
                table: "happening_posts");

            migrationBuilder.CreateIndex(
                name: "ix_happening_posts_kind_thing_id",
                table: "happening_posts",
                columns: new[] { "kind", "thing_id" },
                unique: true);
        }
    }
}
