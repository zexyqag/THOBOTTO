using THOBOTTO.Access;
using THOBOTTO.Panel;

namespace THOBOTTO.Moderation;

public static class ModerationStartup
{
    public static IServiceCollection AddModeration(this IServiceCollection services)
        => services
            .AddSingleton<CaseBook>()
            .AddSingleton<ModActions>()
            .AddSingleton<BanAppeals>()
            .AddSingleton<AutoModSetup>()
            .AddHostedService<ModTimers>()
            .AddSingleton(new GuildNavEntry("moderation", "Moderation", GuildNavGroup.Manage, 30, BotPermissions.ModWarn))
            .AddSingleton(new GuildNavEntry("automod", "AutoMod", GuildNavGroup.Manage, 40, BotPermissions.ModManage));
}
