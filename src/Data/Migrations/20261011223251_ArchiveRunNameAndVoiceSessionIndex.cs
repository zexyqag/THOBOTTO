using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class ArchiveRunNameAndVoiceSessionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "name",
                table: "backfill_runs",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_voice_sessions_left_at",
                table: "voice_sessions",
                column: "left_at",
                filter: "left_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_voice_sessions_left_at",
                table: "voice_sessions");

            migrationBuilder.DropColumn(
                name: "name",
                table: "backfill_runs");
        }
    }
}
