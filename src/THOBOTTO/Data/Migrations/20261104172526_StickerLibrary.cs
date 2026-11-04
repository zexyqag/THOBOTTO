using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class StickerLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "later_posts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    channel_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: true),
                    file = table.Column<byte[]>(type: "bytea", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_later_posts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_later_posts_at",
                table: "later_posts",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_later_posts_user_id",
                table: "later_posts",
                column: "user_id");

            // Stickers don't wait for a slot any more: the library holds them. And what came into the library is live
            // from when it was decided, so retiring unused ones counts from then.
            migrationBuilder.Sql("""
                UPDATE expressions SET state = 'live' WHERE state = 'waiting';
                UPDATE expressions SET live_at = decided_at WHERE state = 'live' AND live_at IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "later_posts");
        }
    }
}
