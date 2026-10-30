using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class PriorityIsAMusicSetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            MovePriority(migrationBuilder, from: "listening", to: "music");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            MovePriority(migrationBuilder, from: "music", to: "listening");
        }

        // "When helpers run out" moved from the voice commands' settings to the music settings; each server's stored choice follows it.
        private static void MovePriority(MigrationBuilder migrationBuilder, string from, string to)
        {
            migrationBuilder.Sql($$"""
                INSERT INTO module_settings (guild_id, module, "json")
                SELECT f.guild_id, '{{to}}', jsonb_build_object('priority', f."json"->'priority')
                FROM module_settings f
                WHERE f.module = '{{from}}' AND f."json" ? 'priority'
                  AND NOT EXISTS (SELECT 1 FROM module_settings t WHERE t.guild_id = f.guild_id AND t.module = '{{to}}');
                UPDATE module_settings t SET "json" = jsonb_set(t."json", '{priority}', f."json"->'priority')
                FROM module_settings f
                WHERE t.module = '{{to}}' AND f.module = '{{from}}' AND f.guild_id = t.guild_id AND f."json" ? 'priority';
                UPDATE module_settings SET "json" = "json" - 'priority' WHERE module = '{{from}}';
                """);
        }
    }
}
