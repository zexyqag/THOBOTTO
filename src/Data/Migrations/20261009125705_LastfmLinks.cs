using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class LastfmLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lastfm_links",
                columns: table => new
                {
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    username = table.Column<string>(type: "text", nullable: false),
                    protected_session_key = table.Column<string>(type: "text", nullable: false),
                    scrobbling = table.Column<bool>(type: "boolean", nullable: false),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lastfm_links", x => x.user_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lastfm_links");
        }
    }
}
