using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class KaomojiInMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "kaomoji_preferences",
                columns: table => new
                {
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    post_as_me = table.Column<bool>(type: "boolean", nullable: false),
                    in_messages = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kaomoji_preferences", x => x.user_id);
                });

            migrationBuilder.CreateTable(
                name: "member_posts",
                columns: table => new
                {
                    message_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    channel_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    thread_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_member_posts", x => x.message_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_member_posts_guild_id_user_id",
                table: "member_posts",
                columns: new[] { "guild_id", "user_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "kaomoji_preferences");

            migrationBuilder.DropTable(
                name: "member_posts");
        }
    }
}
