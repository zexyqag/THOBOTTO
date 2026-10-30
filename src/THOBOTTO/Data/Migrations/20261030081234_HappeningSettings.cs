using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class HappeningSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Events taken in from Discord's calendar go to the happenings forum: where a channel was set, taking them
            // in stays on. The voting channel for proposals is gone (proposals go where they're made and to the forum).
            migrationBuilder.Sql("""
                UPDATE module_settings SET json = (json - 'importChannelId') || jsonb_build_object('importDiscordEvents', json->>'importChannelId' IS NOT NULL)
                WHERE module = 'events' AND json ? 'importChannelId';
                UPDATE module_settings SET json = json - 'voteChannelId' WHERE module = 'emojis' AND json ? 'voteChannelId';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE module_settings SET json = json - 'importDiscordEvents' WHERE module = 'events';");
        }
    }
}
