using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class ServerRemoteControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "announce_sessions",
                table: "game_servers",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "rcon_address",
                table: "game_servers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "rcon_inside",
                table: "game_servers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "rcon_password",
                table: "game_servers",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "announce_sessions",
                table: "game_servers");

            migrationBuilder.DropColumn(
                name: "rcon_address",
                table: "game_servers");

            migrationBuilder.DropColumn(
                name: "rcon_inside",
                table: "game_servers");

            migrationBuilder.DropColumn(
                name: "rcon_password",
                table: "game_servers");
        }
    }
}
