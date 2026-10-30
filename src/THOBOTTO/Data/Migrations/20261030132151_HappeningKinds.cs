using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class HappeningKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The forum's switch per kind becomes the list of kinds switched off (a kind added later starts on).
            migrationBuilder.Sql($"""
                UPDATE module_settings SET json = (json - {Keys}) || jsonb_build_object('off', (
                    SELECT coalesce(jsonb_agg(k.kind ORDER BY k.n), '[]'::jsonb) FROM (VALUES {Kinds}) AS k(n, key, kind) WHERE json->>k.key = 'false'))
                WHERE module = 'happenings';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($$"""
                UPDATE module_settings SET json = (json - 'off') || (
                    SELECT coalesce(jsonb_object_agg(k.key, false), '{}'::jsonb) FROM (VALUES {{Kinds}}) AS k(n, key, kind) WHERE json->'off' ? k.kind)
                WHERE module = 'happenings';
                """);
        }

        // Each switch's key and the kind it was for, in the page's old order.
        private const string Kinds = "(1, 'events', 'event'), (2, 'sessions', 'session'), (3, 'bets', 'bet'), (4, 'emoji', 'emoji'), (5, 'sounds', 'sound'), (6, 'wrapped', 'wrapped'), (7, 'polls', 'poll'), (8, 'quizzes', 'quiz')";

        private const string Keys = "'events' - 'sessions' - 'bets' - 'emoji' - 'sounds' - 'wrapped' - 'polls' - 'quizzes'";
    }
}
