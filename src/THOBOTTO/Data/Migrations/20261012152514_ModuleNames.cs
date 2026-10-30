using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class ModuleNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE enabled_modules SET module = 'voice-channels' WHERE module = 'voice';
                UPDATE enabled_modules SET module = 'game-servers' WHERE module = 'servers';
                UPDATE enabled_modules SET module = 'voice-commands' WHERE module = 'listening';
                UPDATE enabled_modules SET module = 'helper-voices' WHERE module = 'voices';
                UPDATE module_settings SET module = 'voice-channels' WHERE module = 'voice';
                UPDATE module_settings SET module = 'game-servers' WHERE module = 'servers';
                UPDATE module_settings SET module = 'voice-commands' WHERE module = 'listening';
                UPDATE module_settings SET module = 'helper-voices' WHERE module = 'voices';
                UPDATE permission_grants SET permission = 'game-servers.manage' WHERE permission = 'servers.manage';
                UPDATE permission_grants SET permission = 'voice-channels.manage' WHERE permission = 'voice.hubs';
                """);
            // The backups kept so far are in the older format, with the old ids inside; none can be restored anymore.
            migrationBuilder.Sql("DELETE FROM stored_backups;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE enabled_modules SET module = 'voice' WHERE module = 'voice-channels';
                UPDATE enabled_modules SET module = 'servers' WHERE module = 'game-servers';
                UPDATE enabled_modules SET module = 'listening' WHERE module = 'voice-commands';
                UPDATE enabled_modules SET module = 'voices' WHERE module = 'helper-voices';
                UPDATE module_settings SET module = 'voice' WHERE module = 'voice-channels';
                UPDATE module_settings SET module = 'servers' WHERE module = 'game-servers';
                UPDATE module_settings SET module = 'listening' WHERE module = 'voice-commands';
                UPDATE module_settings SET module = 'voices' WHERE module = 'helper-voices';
                UPDATE permission_grants SET permission = 'servers.manage' WHERE permission = 'game-servers.manage';
                UPDATE permission_grants SET permission = 'voice.hubs' WHERE permission = 'voice-channels.manage';
                """);
            // The backups kept since are in the newer format, which the build this returns to can't read.
            migrationBuilder.Sql("DELETE FROM stored_backups;");
        }
    }
}
