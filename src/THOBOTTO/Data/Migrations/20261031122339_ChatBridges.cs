using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChatBridges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "bridge_channel_id",
                table: "game_servers",
                type: "numeric(20,0)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "bridge_into",
                table: "game_servers",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<decimal>(
                name: "bridge_webhook_id",
                table: "game_servers",
                type: "numeric(20,0)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bridge_webhook_token",
                table: "game_servers",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bridge_channel_id",
                table: "game_servers");

            migrationBuilder.DropColumn(
                name: "bridge_into",
                table: "game_servers");

            migrationBuilder.DropColumn(
                name: "bridge_webhook_id",
                table: "game_servers");

            migrationBuilder.DropColumn(
                name: "bridge_webhook_token",
                table: "game_servers");
        }
    }
}
