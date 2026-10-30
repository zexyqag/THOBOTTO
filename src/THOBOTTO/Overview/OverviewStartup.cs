using THOBOTTO.Access;
using THOBOTTO.Panel;

namespace THOBOTTO.Overview;

public static class OverviewStartup
{
    public static IServiceCollection AddOverview(this IServiceCollection services)
        => services
            .AddSingleton<OverviewBoard>()
            .AddSingleton<IWidgetKind, TextWidget>()
            .AddHostedService(s => s.GetRequiredService<OverviewBoard>())
            .AddSingleton(new GuildNavEntry("overview", "Overview", GuildNavGroup.Manage, 25, BotPermissions.ManageOverview, Module: OverviewBoard.ModuleId));
}
