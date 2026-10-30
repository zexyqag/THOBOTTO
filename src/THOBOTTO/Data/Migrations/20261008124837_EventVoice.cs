using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class EventVoice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "discord_event_id",
                table: "events",
                type: "numeric(20,0)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "voice_channel_id",
                table: "events",
                type: "numeric(20,0)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "voice_mode",
                table: "events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "wants_discord_event",
                table: "events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "voice_mode",
                table: "event_series",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "wants_discord_event",
                table: "event_series",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "discord_event_id",
                table: "events");

            migrationBuilder.DropColumn(
                name: "voice_channel_id",
                table: "events");

            migrationBuilder.DropColumn(
                name: "voice_mode",
                table: "events");

            migrationBuilder.DropColumn(
                name: "wants_discord_event",
                table: "events");

            migrationBuilder.DropColumn(
                name: "voice_mode",
                table: "event_series");

            migrationBuilder.DropColumn(
                name: "wants_discord_event",
                table: "event_series");
        }
    }
}
