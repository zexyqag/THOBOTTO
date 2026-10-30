using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlayHistoryAndVoiceSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "play_records",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    voice_channel_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    helper_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    personality = table.Column<string>(type: "text", nullable: true),
                    artist = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    uri = table.Column<string>(type: "text", nullable: true),
                    length_ms = table.Column<long>(type: "bigint", nullable: false),
                    played_ms = table.Column<long>(type: "bigint", nullable: false),
                    skipped = table.Column<bool>(type: "boolean", nullable: false),
                    autoplay = table.Column<bool>(type: "boolean", nullable: false),
                    requested_by = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_play_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "voice_sessions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    channel_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    left_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_voice_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "play_listeners",
                columns: table => new
                {
                    play_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_play_listeners", x => new { x.play_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_play_listeners_play_records_play_id",
                        column: x => x.play_id,
                        principalTable: "play_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_play_listeners_user_id",
                table: "play_listeners",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_play_records_guild_id_started_at",
                table: "play_records",
                columns: new[] { "guild_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_voice_sessions_guild_id_joined_at",
                table: "voice_sessions",
                columns: new[] { "guild_id", "joined_at" });

            migrationBuilder.CreateIndex(
                name: "ix_voice_sessions_guild_id_user_id",
                table: "voice_sessions",
                columns: new[] { "guild_id", "user_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "play_listeners");

            migrationBuilder.DropTable(
                name: "voice_sessions");

            migrationBuilder.DropTable(
                name: "play_records");
        }
    }
}
