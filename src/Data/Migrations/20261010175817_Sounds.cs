using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class Sounds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "join_sounds",
                columns: table => new
                {
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    sound_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_join_sounds", x => new { x.guild_id, x.user_id });
                });

            migrationBuilder.CreateTable(
                name: "sound_votes",
                columns: table => new
                {
                    sound_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    up = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sound_votes", x => new { x.sound_id, x.user_id });
                });

            migrationBuilder.CreateTable(
                name: "sounds",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    creator_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    proposer_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    audio = table.Column<byte[]>(type: "bytea", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    milliseconds = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    paid = table.Column<double>(type: "double precision", nullable: false),
                    vote_channel_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    vote_message_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    proposed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    vote_ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    discord_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    plays = table.Column<int>(type: "integer", nullable: false),
                    last_played_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sounds", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sounds_guild_id_state",
                table: "sounds",
                columns: new[] { "guild_id", "state" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "join_sounds");

            migrationBuilder.DropTable(
                name: "sound_votes");

            migrationBuilder.DropTable(
                name: "sounds");
        }
    }
}
