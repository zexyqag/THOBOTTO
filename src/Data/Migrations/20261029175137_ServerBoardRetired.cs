using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class ServerBoardRetired : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Each board becomes a widget after its channel's others: the servers a card each, two abreast, in the
            // board's green. Where the servers were on, the overview channels are too.
            migrationBuilder.Sql("""
                INSERT INTO overview_widgets (guild_id, channel_id, "order", colour, style, layout, created_at)
                SELECT s.guild_id, s.board_channel_id,
                    (SELECT COUNT(*) FROM overview_widgets w WHERE w.guild_id = s.guild_id AND w.channel_id = s.board_channel_id)::int,
                    5763719, 'columns-rows', '[[{"kind":"game-servers","options":{"show":"cards","columns":"2"}}]]', now()
                FROM server_settings s WHERE s.board_channel_id IS NOT NULL;
                INSERT INTO enabled_modules (guild_id, module)
                SELECT s.guild_id, 'overview' FROM server_settings s
                WHERE s.board_channel_id IS NOT NULL
                    AND EXISTS (SELECT 1 FROM enabled_modules e WHERE e.guild_id = s.guild_id AND e.module = 'game-servers')
                ON CONFLICT DO NOTHING;
                """);

            migrationBuilder.DropColumn(
                name: "board_channel_id",
                table: "server_settings");

            migrationBuilder.DropColumn(
                name: "message_id",
                table: "game_servers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "board_channel_id",
                table: "server_settings",
                type: "numeric(20,0)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "message_id",
                table: "game_servers",
                type: "numeric(20,0)",
                nullable: true);
        }
    }
}
