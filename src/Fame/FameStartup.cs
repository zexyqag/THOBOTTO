using THOBOTTO.Archive;
using THOBOTTO.Members;
using THOBOTTO.Overview;
using THOBOTTO.Panel;

namespace THOBOTTO.Fame;

public static class FameStartup
{
    public static IServiceCollection AddFame(this IServiceCollection services)
        => services
            .AddSingleton<HallOfFame>()
            .AddSingleton<IExportSource, FameExport>()
            .AddSingleton<IWidgetKind, LatestFameWidget>()
            .AddSingleton<IMemberData, FameMemberData>()
            .AddSingleton(new GuildNavEntry("fame", "Hall of fame", GuildNavGroup.Community, 80, Module: HallOfFame.ModuleId));
}
