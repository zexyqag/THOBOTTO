using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class Happenings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "happening_posts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    guild_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    thing_id = table.Column<long>(type: "bigint", nullable: false),
                    forum_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    thread_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    gone = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_happening_posts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_happening_posts_kind_thing_id",
                table: "happening_posts",
                columns: new[] { "kind", "thing_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "happening_posts");
        }
    }
}
