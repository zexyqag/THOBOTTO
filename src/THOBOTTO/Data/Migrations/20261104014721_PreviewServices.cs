using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class PreviewServices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Reddit blocked rxddit and vxtiktok shut down (November 2026): saved lists follow the defaults' new services.
            migrationBuilder.Sql("""
                UPDATE module_settings
                SET json = replace(replace(json::text, 'reddit.com=rxddit.com', 'reddit.com=vxreddit.com'), 'tiktok.com=vxtiktok.com', 'tiktok.com=tnktok.com')::jsonb
                WHERE module = 'links';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
