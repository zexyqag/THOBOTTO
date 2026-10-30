using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class JoinSoundsMany : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_join_sounds",
                table: "join_sounds");

            migrationBuilder.AddPrimaryKey(
                name: "pk_join_sounds",
                table: "join_sounds",
                columns: new[] { "guild_id", "user_id", "sound_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_join_sounds",
                table: "join_sounds");

            migrationBuilder.AddPrimaryKey(
                name: "pk_join_sounds",
                table: "join_sounds",
                columns: new[] { "guild_id", "user_id" });
        }
    }
}
