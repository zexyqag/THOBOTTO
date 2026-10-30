using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class SoundSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<byte[]>(
                name: "audio",
                table: "sounds",
                type: "bytea",
                nullable: true,
                oldClrType: typeof(byte[]),
                oldType: "bytea");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "discord_removed_at",
                table: "sounds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "removed_at",
                table: "sounds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "sound_plays",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    sound_id = table.Column<long>(type: "bigint", nullable: false),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    on_discord = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sound_plays", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sound_plays_guild_id_at",
                table: "sound_plays",
                columns: new[] { "guild_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_sound_plays_sound_id_at",
                table: "sound_plays",
                columns: new[] { "sound_id", "at" });
            migrationBuilder.Sql("UPDATE sounds SET removed_at = coalesce(decided_at, proposed_at) WHERE state = 'removed'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sound_plays");

            migrationBuilder.DropColumn(
                name: "discord_removed_at",
                table: "sounds");

            migrationBuilder.DropColumn(
                name: "removed_at",
                table: "sounds");

            migrationBuilder.AlterColumn<byte[]>(
                name: "audio",
                table: "sounds",
                type: "bytea",
                nullable: false,
                defaultValue: new byte[0],
                oldClrType: typeof(byte[]),
                oldType: "bytea",
                oldNullable: true);
        }
    }
}
