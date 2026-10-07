using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class DynamicVoice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dynamic_voice_channels",
                columns: table => new
                {
                    channel_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    owner_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dynamic_voice_channels", x => x.channel_id);
                });

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
                name: "ix_dynamic_voice_channels_guild_id",
                table: "dynamic_voice_channels",
                column: "guild_id");

            migrationBuilder.CreateIndex(
                name: "ix_voice_hubs_guild_id",
                table: "voice_hubs",
                column: "guild_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dynamic_voice_channels");

            migrationBuilder.DropTable(
                name: "voice_hubs");
        }
    }
}
