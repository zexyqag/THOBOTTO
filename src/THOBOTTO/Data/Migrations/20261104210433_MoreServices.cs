using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THOBOTTO.Data.Migrations
{
    /// <inheritdoc />
    public partial class MoreServices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Instagram has services that preview again, Reddit a second one, and Tumblr and Bilibili join (November 2026): a
            // list as it came takes the new one; an edited one is the server's own.
            migrationBuilder.Sql("""
                UPDATE module_settings
                SET json = replace(json::text,
                    'x.com=fixupx.com,vxtwitter.com\ntwitter.com=fixupx.com,vxtwitter.com\ntiktok.com=tnktok.com,tiktxk.com\nreddit.com=vxreddit.com\nbsky.app=fxbsky.app,bskyx.app\ninstagram.com="',
                    'x.com=fixupx.com,vxtwitter.com\ntwitter.com=fixupx.com,vxtwitter.com\ntiktok.com=tnktok.com,tiktxk.com\nreddit.com=vxreddit.com,fxreddit.seria.moe\nbsky.app=fxbsky.app,bskyx.app\ninstagram.com=eeinstagram.com,vxinstagram.com,oginstagram.com\ntumblr.com=tpmblr.com\nbilibili.com=vxbilibili.com,fxbilibili.seria.moe"')::jsonb
                WHERE module = 'links';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
