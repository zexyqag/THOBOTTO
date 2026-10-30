using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class PointsEarned : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "earned",
                table: "point_entries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // What counted as earned was a list of kinds; now each award says so.
            migrationBuilder.Sql("UPDATE point_entries SET earned = true WHERE amount > 0 AND kind IN ('activity', 'fame', 'royalty', 'kudos', 'expression', 'quiz');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "earned",
                table: "point_entries");
        }
    }
}
