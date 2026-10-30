using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecurringSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "game_id",
                table: "event_series",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mode",
                table: "event_series",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "game_id",
                table: "event_series");

            migrationBuilder.DropColumn(
                name: "mode",
                table: "event_series");
        }
    }
}
