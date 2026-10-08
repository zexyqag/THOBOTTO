using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class Archive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "archived_attachments",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    message_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: true),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    url_fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    stored_key = table.Column<string>(type: "text", nullable: true),
                    stored_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_archived_attachments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "archived_messages",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    channel_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    author_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content = table.Column<string>(type: "text", nullable: true),
                    raw = table.Column<string>(type: "jsonb", nullable: true),
                    edited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    purged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_archived_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "message_versions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    message_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    content = table.Column<string>(type: "text", nullable: true),
                    raw = table.Column<string>(type: "jsonb", nullable: true),
                    replaced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_message_versions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_archived_attachments_message_id",
                table: "archived_attachments",
                column: "message_id");

            migrationBuilder.CreateIndex(
                name: "ix_archived_messages_guild_id_author_id",
                table: "archived_messages",
                columns: new[] { "guild_id", "author_id" });

            migrationBuilder.CreateIndex(
                name: "ix_archived_messages_guild_id_channel_id_id",
                table: "archived_messages",
                columns: new[] { "guild_id", "channel_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_message_versions_message_id",
                table: "message_versions",
                column: "message_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "archived_attachments");

            migrationBuilder.DropTable(
                name: "archived_messages");

            migrationBuilder.DropTable(
                name: "message_versions");
        }
    }
}
