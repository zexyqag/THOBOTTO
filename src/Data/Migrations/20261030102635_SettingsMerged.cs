using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class SettingsMerged : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Appeals and quotes now have a switch of their own, the moderation log and the showcase taking them by
            // default: servers go on as they were (appeals and quotes only where a channel was set for them).
            migrationBuilder.Sql("""
                UPDATE module_settings SET json = json || jsonb_build_object('takeAppeals', json->>'appealsChannelId' IS NOT NULL) WHERE module = 'moderation';
                UPDATE module_settings SET json = json || jsonb_build_object('postQuotes', json->>'channelId' IS NOT NULL) WHERE module = 'quotes';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE module_settings SET json = json - 'takeAppeals' WHERE module = 'moderation';
                UPDATE module_settings SET json = json - 'postQuotes' WHERE module = 'quotes';
                """);
        }
    }
}
