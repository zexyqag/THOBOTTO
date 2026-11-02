using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlanOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "move",
                table: "group_proposals",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "move",
                table: "events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "decide",
                table: "event_series",
                type: "text",
                nullable: false,
                defaultValue: "closes");

            migrationBuilder.AddColumn<int>(
                name: "enough",
                table: "event_series",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "move",
                table: "event_series",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "event_series_games",
                columns: table => new
                {
                    series_id = table.Column<long>(type: "bigint", nullable: false),
                    game_id = table.Column<long>(type: "bigint", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    emoji = table.Column<string>(type: "text", nullable: true),
                    role_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    players = table.Column<int>(type: "integer", nullable: true),
                    title = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_series_games", x => new { x.series_id, x.game_id });
                    table.ForeignKey(
                        name: "fk_event_series_games_event_series_series_id",
                        column: x => x.series_id,
                        principalTable: "event_series",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "event_series_games");

            migrationBuilder.DropColumn(
                name: "move",
                table: "group_proposals");

            migrationBuilder.DropColumn(
                name: "move",
                table: "events");

            migrationBuilder.DropColumn(
                name: "decide",
                table: "event_series");

            migrationBuilder.DropColumn(
                name: "enough",
                table: "event_series");

            migrationBuilder.DropColumn(
                name: "move",
                table: "event_series");
        }
    }
}
