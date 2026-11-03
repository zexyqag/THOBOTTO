using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class EmojiLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "app_emoji_id",
                table: "expressions",
                type: "numeric(20,0)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "app_emoji_name",
                table: "expressions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "discord_removed_at",
                table: "expressions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "pinned",
                table: "expressions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "expression_use_days",
                columns: table => new
                {
                    expression_id = table.Column<long>(type: "bigint", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_expression_use_days", x => new { x.expression_id, x.day });
                });

            // Emoji don't wait for a slot any more: the library holds them.
            migrationBuilder.Sql("UPDATE expressions SET state = 'live' WHERE kind = 'emoji' AND state = 'waiting';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "expression_use_days");

            migrationBuilder.DropColumn(
                name: "app_emoji_id",
                table: "expressions");

            migrationBuilder.DropColumn(
                name: "app_emoji_name",
                table: "expressions");

            migrationBuilder.DropColumn(
                name: "discord_removed_at",
                table: "expressions");

            migrationBuilder.DropColumn(
                name: "pinned",
                table: "expressions");
        }
    }
}
