using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class GameServerDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "join_address",
                table: "game_servers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "modpack",
                table: "game_servers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "modpack_link",
                table: "game_servers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "modpack_version",
                table: "game_servers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "note",
                table: "game_servers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "password",
                table: "game_servers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "password_for",
                table: "game_servers",
                type: "text",
                nullable: false,
                defaultValue: "anyone");

            migrationBuilder.AddColumn<decimal>(
                name: "password_role_id",
                table: "game_servers",
                type: "numeric(20,0)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "join_address",
                table: "game_servers");

            migrationBuilder.DropColumn(
                name: "modpack",
                table: "game_servers");

            migrationBuilder.DropColumn(
                name: "modpack_link",
                table: "game_servers");

            migrationBuilder.DropColumn(
                name: "modpack_version",
                table: "game_servers");

            migrationBuilder.DropColumn(
                name: "note",
                table: "game_servers");

            migrationBuilder.DropColumn(
                name: "password",
                table: "game_servers");

            migrationBuilder.DropColumn(
                name: "password_for",
                table: "game_servers");

            migrationBuilder.DropColumn(
                name: "password_role_id",
                table: "game_servers");
        }
    }
}
