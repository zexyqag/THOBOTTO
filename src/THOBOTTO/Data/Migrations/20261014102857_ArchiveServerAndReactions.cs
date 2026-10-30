using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class ArchiveServerAndReactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "archived_assets",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    hash = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: true),
                    stored_key = table.Column<string>(type: "text", nullable: true),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_archived_assets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "archived_channels",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    parent_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    type = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    topic = table.Column<string>(type: "text", nullable: true),
                    position = table.Column<int>(type: "integer", nullable: true),
                    nsfw = table.Column<bool>(type: "boolean", nullable: false),
                    rate_limit = table.Column<int>(type: "integer", nullable: true),
                    flags = table.Column<int>(type: "integer", nullable: false),
                    available_tags = table.Column<string>(type: "jsonb", nullable: true),
                    applied_tags = table.Column<string>(type: "jsonb", nullable: true),
                    owner_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    thread_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    archived = table.Column<bool>(type: "boolean", nullable: false),
                    locked = table.Column<bool>(type: "boolean", nullable: false),
                    permission_overwrites = table.Column<string>(type: "jsonb", nullable: true),
                    raw = table.Column<string>(type: "jsonb", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_archived_channels", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "archived_members",
                columns: table => new
                {
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    username = table.Column<string>(type: "text", nullable: false),
                    global_name = table.Column<string>(type: "text", nullable: true),
                    nick = table.Column<string>(type: "text", nullable: true),
                    avatar_hash = table.Column<string>(type: "text", nullable: true),
                    guild_avatar_hash = table.Column<string>(type: "text", nullable: true),
                    role_ids = table.Column<string>(type: "jsonb", nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    premium_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    bot = table.Column<bool>(type: "boolean", nullable: false),
                    raw = table.Column<string>(type: "jsonb", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    left_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_archived_members", x => new { x.guild_id, x.user_id });
                });

            migrationBuilder.CreateTable(
                name: "archived_poll_votes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    message_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    answer_id = table.Column<int>(type: "integer", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_archived_poll_votes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "archived_reactions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    message_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    user_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    emoji_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    emoji_name = table.Column<string>(type: "text", nullable: false),
                    burst = table.Column<bool>(type: "boolean", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_archived_reactions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "archived_roles",
                columns: table => new
                {
                    id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    color = table.Column<int>(type: "integer", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    permissions = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    hoist = table.Column<bool>(type: "boolean", nullable: false),
                    mentionable = table.Column<bool>(type: "boolean", nullable: false),
                    icon_hash = table.Column<string>(type: "text", nullable: true),
                    managed = table.Column<bool>(type: "boolean", nullable: false),
                    raw = table.Column<string>(type: "jsonb", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_archived_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "guild_snapshots",
                columns: table => new
                {
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    taken_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tried_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    json = table.Column<string>(type: "jsonb", nullable: true),
                    problem = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guild_snapshots", x => new { x.guild_id, x.kind });
                });

            migrationBuilder.CreateIndex(
                name: "ix_archived_assets_guild_id_kind_entity_id_hash",
                table: "archived_assets",
                columns: new[] { "guild_id", "kind", "entity_id", "hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_archived_channels_guild_id",
                table: "archived_channels",
                column: "guild_id");

            migrationBuilder.CreateIndex(
                name: "ix_archived_poll_votes_message_id",
                table: "archived_poll_votes",
                column: "message_id");

            migrationBuilder.CreateIndex(
                name: "ix_archived_reactions_message_id",
                table: "archived_reactions",
                column: "message_id");

            migrationBuilder.CreateIndex(
                name: "ix_archived_roles_guild_id",
                table: "archived_roles",
                column: "guild_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "archived_assets");

            migrationBuilder.DropTable(
                name: "archived_channels");

            migrationBuilder.DropTable(
                name: "archived_members");

            migrationBuilder.DropTable(
                name: "archived_poll_votes");

            migrationBuilder.DropTable(
                name: "archived_reactions");

            migrationBuilder.DropTable(
                name: "archived_roles");

            migrationBuilder.DropTable(
                name: "guild_snapshots");
        }
    }
}
