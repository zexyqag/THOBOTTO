using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class TwitchChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "streamer",
                table: "live_streams",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "twitch_id",
                table: "live_streams",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "twitch_channels",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    twitch_id = table.Column<string>(type: "text", nullable: false),
                    login = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    added_by = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_twitch_channels", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_twitch_channels_guild_id_twitch_id",
                table: "twitch_channels",
                columns: new[] { "guild_id", "twitch_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "twitch_channels");

            migrationBuilder.DropColumn(
                name: "streamer",
                table: "live_streams");

            migrationBuilder.DropColumn(
                name: "twitch_id",
                table: "live_streams");
        }
    }
}
