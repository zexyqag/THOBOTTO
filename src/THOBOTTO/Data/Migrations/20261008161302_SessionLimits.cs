using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class SessionLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "players",
                table: "games",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "capacity",
                table: "events",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "first_part_id",
                table: "events",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "players",
                table: "games");

            migrationBuilder.DropColumn(
                name: "capacity",
                table: "events");

            migrationBuilder.DropColumn(
                name: "first_part_id",
                table: "events");
        }
    }
}
