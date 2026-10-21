using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class VoiceHubsInSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The hubs move into the module's settings (the panel shows them there now), then the table goes. Stored
            // backups are of the format before (hubs of their own) and are refused from here on, so they go too.
            migrationBuilder.Sql("""
                INSERT INTO module_settings (guild_id, module, json)
                SELECT guild_id, 'voice-channels', jsonb_build_object('hubChannelIds', jsonb_agg(channel_id))
                FROM voice_hubs GROUP BY guild_id
                ON CONFLICT (guild_id, module) DO UPDATE SET json = module_settings.json || EXCLUDED.json;
                DELETE FROM stored_backups;
                """);
            migrationBuilder.DropTable(
                name: "voice_hubs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "voice_hubs",
                columns: table => new
                {
                    channel_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_voice_hubs", x => x.channel_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_voice_hubs_guild_id",
                table: "voice_hubs",
                column: "guild_id");
        }
    }
}
