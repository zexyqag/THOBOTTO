using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class OwnerRenames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "declined_at",
                table: "renames",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "done_at",
                table: "renames",
                type: "timestamp with time zone",
                nullable: true);
            migrationBuilder.Sql("UPDATE renames SET done_at = created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "declined_at",
                table: "renames");

            migrationBuilder.DropColumn(
                name: "done_at",
                table: "renames");
        }
    }
}
