using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class GamePickersRetired : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Each picker becomes a widget after its channel's others, the Game roles block's buttons alone; where
            // games were on, the overview channels are too.
            migrationBuilder.Sql("""
                INSERT INTO overview_widgets (guild_id, channel_id, "order", style, layout, created_at)
                SELECT p.guild_id, p.channel_id,
                    ((SELECT COUNT(*) FROM overview_widgets w WHERE w.guild_id = p.guild_id AND w.channel_id = p.channel_id)
                        + ROW_NUMBER() OVER (PARTITION BY p.guild_id, p.channel_id ORDER BY p.message_id) - 1)::int,
                    'cards-lines', '[[{"kind":"game-roles","options":{"show":"buttons"}}]]', now()
                FROM game_pickers p;
                INSERT INTO enabled_modules (guild_id, module)
                SELECT DISTINCT p.guild_id, 'overview' FROM game_pickers p
                WHERE EXISTS (SELECT 1 FROM enabled_modules e WHERE e.guild_id = p.guild_id AND e.module = 'games')
                ON CONFLICT DO NOTHING;
                """);

            migrationBuilder.DropTable(
                name: "game_pickers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "game_pickers",
                columns: table => new
                {
                    message_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    channel_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_game_pickers", x => x.message_id);
                });
        }
    }
}
