using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class ArchivedMessageTimeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_archived_messages_guild_id_created_at",
                table: "archived_messages",
                columns: new[] { "guild_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_archived_messages_guild_id_created_at",
                table: "archived_messages");
        }
    }
}
