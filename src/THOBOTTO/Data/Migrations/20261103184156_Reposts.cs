using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class Reposts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "kaomoji_preferences");

            migrationBuilder.CreateTable(
                name: "posting_preferences",
                columns: table => new
                {
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    kaomoji_as_me = table.Column<bool>(type: "boolean", nullable: false),
                    kao_words = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_posting_preferences", x => x.user_id);
                });

            // Faces were posted under members' names wherever kaomoji was on; that now needs its own module.
            migrationBuilder.Sql("""
                INSERT INTO enabled_modules (guild_id, module)
                SELECT guild_id, 'reposts' FROM enabled_modules WHERE module = 'kaomoji'
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "posting_preferences");

            migrationBuilder.CreateTable(
                name: "kaomoji_preferences",
                columns: table => new
                {
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    in_messages = table.Column<bool>(type: "boolean", nullable: false),
                    post_as_me = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kaomoji_preferences", x => x.user_id);
                });
        }
    }
}
