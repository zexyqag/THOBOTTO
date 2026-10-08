using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class Moderation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mod_cases",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    moderator_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    details = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    pardoned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    pardoned_by_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    pardon_reason = table.Column<string>(type: "text", nullable: true),
                    log_channel_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    log_message_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mod_cases", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mod_cases_guild_id_number",
                table: "mod_cases",
                columns: new[] { "guild_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mod_cases_guild_id_target_id",
                table: "mod_cases",
                columns: new[] { "guild_id", "target_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mod_cases");
        }
    }
}
