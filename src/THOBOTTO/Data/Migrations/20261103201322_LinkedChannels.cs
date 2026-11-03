using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class LinkedChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "channel_links",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    channel_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    language = table.Column<string>(type: "text", nullable: true),
                    other_guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    other_channel_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    other_language = table.Column<string>(type: "text", nullable: true),
                    both = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channel_links", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "linked_copies",
                columns: table => new
                {
                    source_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    copy_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    language = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_linked_copies", x => new { x.source_id, x.copy_id });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_links");

            migrationBuilder.DropTable(
                name: "linked_copies");
        }
    }
}
