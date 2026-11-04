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
            // Reddit blocked rxddit and vxtiktok shut down (November 2026), and each site now has services to try in turn:
            // a list as it came takes the new one, an edited one loses the two dead services.
            migrationBuilder.Sql("""
                UPDATE module_settings
                SET json = replace(replace(replace(json::text,
                    'x.com=fixupx.com\ntwitter.com=fixupx.com\ntiktok.com=vxtiktok.com\nreddit.com=rxddit.com\nbsky.app=fxbsky.app',
                    'x.com=fixupx.com,vxtwitter.com\ntwitter.com=fixupx.com,vxtwitter.com\ntiktok.com=tnktok.com,tiktxk.com\nreddit.com=vxreddit.com\nbsky.app=fxbsky.app,bskyx.app\ninstagram.com='),
                    'tiktok.com=vxtiktok.com', 'tiktok.com=tnktok.com,tiktxk.com'),
                    'reddit.com=rxddit.com', 'reddit.com=vxreddit.com')::jsonb
                WHERE module = 'links';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
