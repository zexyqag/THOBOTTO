using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class PinnedSounds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "pinned",
                table: "sounds",
                type: "boolean",
                nullable: false,
                defaultValue: false);
            migrationBuilder.Sql("UPDATE sounds SET pinned = true WHERE discord_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pinned",
                table: "sounds");
        }
    }
}
