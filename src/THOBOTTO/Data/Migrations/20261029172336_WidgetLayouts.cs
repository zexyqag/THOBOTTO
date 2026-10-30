using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class WidgetLayouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "title",
                table: "overview_widgets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "colour",
                table: "overview_widgets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "style",
                table: "overview_widgets",
                type: "text",
                nullable: false,
                defaultValue: "cards-lines");

            migrationBuilder.AddColumn<string>(
                name: "layout",
                table: "overview_widgets",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            // Each widget so far holds its one block, and is posted anew in the new look.
            migrationBuilder.Sql("""
                UPDATE overview_widgets
                SET layout = json_build_array(json_build_array(json_build_object('kind', kind, 'options', options::json)))::text, shown = NULL;
                """);

            migrationBuilder.DropColumn(
                name: "kind",
                table: "overview_widgets");

            migrationBuilder.DropColumn(
                name: "options",
                table: "overview_widgets");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "overview_widgets",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "options",
                table: "overview_widgets",
                type: "text",
                nullable: false,
                defaultValue: "{}");

            // Back to one block a widget: the first.
            migrationBuilder.Sql("""
                UPDATE overview_widgets
                SET kind = COALESCE(layout::json->0->0->>'kind', ''), options = COALESCE((layout::json->0->0->'options')::text, '{}'), shown = NULL;
                DELETE FROM overview_widgets WHERE kind = '';
                """);

            migrationBuilder.DropColumn(
                name: "title",
                table: "overview_widgets");

            migrationBuilder.DropColumn(
                name: "colour",
                table: "overview_widgets");

            migrationBuilder.DropColumn(
                name: "style",
                table: "overview_widgets");

            migrationBuilder.DropColumn(
                name: "layout",
                table: "overview_widgets");
        }
    }
}
