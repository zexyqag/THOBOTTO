using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class PermissionsAndRenames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "permission_grants",
                columns: table => new
                {
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    role_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    permission = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_permission_grants", x => new { x.guild_id, x.role_id, x.permission });
                });

            migrationBuilder.CreateTable(
                name: "renames",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    actor_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    target_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    old_name = table.Column<string>(type: "text", nullable: true),
                    new_name = table.Column<string>(type: "text", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_renames", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_renames_guild_id_target_id_created_at",
                table: "renames",
                columns: new[] { "guild_id", "target_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "permission_grants");

            migrationBuilder.DropTable(
                name: "renames");
        }
    }
}
