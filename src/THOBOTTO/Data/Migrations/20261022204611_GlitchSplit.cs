using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class GlitchSplit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The one glitch amount became three.
            migrationBuilder.Sql("UPDATE personalities SET effects = (effects - 'glitch') || jsonb_build_object('stutter', effects->'glitch', 'dropout', effects->'glitch', 'crackle', effects->'glitch') WHERE effects ? 'glitch'");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
