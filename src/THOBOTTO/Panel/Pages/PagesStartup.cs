using THOBOTTO.Access;
using THOBOTTO.Members;

namespace THOBOTTO.Panel.Pages;

public static class PagesStartup
{
    public static IServiceCollection AddPages(this IServiceCollection services)
        => services
            .AddSingleton<IntegrationStatus>()
            .AddSingleton(new BotNavEntry("/", "Home", 10))
            .AddSingleton(new BotNavEntry("/status", "Status", 20))
            .AddSingleton(new BotNavEntry("/integrations", "Integrations", 40))
            .AddSingleton(new BotNavEntry("/debug", "Debug", 60))
            .AddSingleton(new GuildNavEntry("debug", "Debug", GuildNavGroup.Manage, 50, BotPermissions.ManagePermissions));

    public static WebApplication MapPages(this WebApplication app)
    {
        app.MapGet("/g/{guildId}/me/data", (HttpContext context, ulong guildId, PanelAccess access, IEnumerable<IMemberData> sources, TimeProvider time)
            => MemberDataDownload.ServeAsync(context, guildId, access, sources, time)).RequireAuthorization();
        return app;
    }
}
