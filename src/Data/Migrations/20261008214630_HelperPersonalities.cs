using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class HelperPersonalities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "helper_profiles");

            migrationBuilder.CreateTable(
                name: "helper_accounts",
                columns: table => new
                {
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    protected_token = table.Column<string>(type: "text", nullable: false),
                    added_by_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_helper_accounts", x => x.user_id);
                });

            migrationBuilder.CreateTable(
                name: "personalities",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    color = table.Column<int>(type: "integer", nullable: true),
                    avatar = table.Column<byte[]>(type: "bytea", nullable: true),
                    avatar_type = table.Column<string>(type: "text", nullable: true),
                    phrases = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_personalities", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "helper_assignments",
                columns: table => new
                {
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    helper_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    personality_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_helper_assignments", x => new { x.guild_id, x.helper_id });
                    table.ForeignKey(
                        name: "fk_helper_assignments_personalities_personality_id",
                        column: x => x.personality_id,
                        principalTable: "personalities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_helper_assignments_personality_id",
                table: "helper_assignments",
                column: "personality_id");

            migrationBuilder.CreateIndex(
                name: "ix_personalities_guild_id",
                table: "personalities",
                column: "guild_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "helper_accounts");

            migrationBuilder.DropTable(
                name: "helper_assignments");

            migrationBuilder.DropTable(
                name: "personalities");

            migrationBuilder.CreateTable(
                name: "helper_profiles",
                columns: table => new
                {
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    color = table.Column<int>(type: "integer", nullable: true),
                    nickname = table.Column<string>(type: "text", nullable: true),
                    phrases = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_helper_profiles", x => x.user_id);
                });
        }
    }
}
