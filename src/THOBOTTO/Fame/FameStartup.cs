using THOBOTTO.Archive;
using THOBOTTO.Members;
using THOBOTTO.Modules;
using THOBOTTO.Overview;
using THOBOTTO.Panel;
using THOBOTTO.Wrapped;

namespace THOBOTTO.Fame;

public static class FameStartup
{
    public static IServiceCollection AddFame(this IServiceCollection services)
        => services
            .AddSingleton<HallOfFame>()
            .AddSingleton<IExportSource, FameExport>()
            .AddSingleton<ISettingsSummary, FameSummary>()
            .AddSingleton<IWrappedSection, FameWrapped>()
            .AddSingleton<IWidgetKind, LatestFameWidget>()
            .AddSingleton<IMemberData, FameMemberData>()
            .AddSingleton(new GuildNavEntry("fame", "Hall of fame", GuildNavGroup.Community, 80, Module: HallOfFame.ModuleId));
}
