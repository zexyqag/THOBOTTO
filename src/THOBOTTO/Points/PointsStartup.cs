using THOBOTTO.Archive;
using THOBOTTO.Members;
using THOBOTTO.Overview;
using THOBOTTO.Panel;
using THOBOTTO.Voice.Commands;
using THOBOTTO.Wrapped;

namespace THOBOTTO.Points;

public static class PointsStartup
{
    public static IServiceCollection AddPoints(this IServiceCollection services)
        => services
            .AddSingleton<PointsEngine>()
            .AddSingleton<IWidgetKind, LeaderboardWidget>()
            .AddHostedService(s => s.GetRequiredService<PointsEngine>())
            .AddSingleton<IVoiceActions, KudosVoiceActions>()
            .AddSingleton<IVoiceActions, PointsVoiceActions>()
            .AddSingleton<IExportSource, PointsExport>()
            .AddSingleton<IWrappedSection, PointsWrapped>()
            .AddSingleton<IMemberData, PointsMemberData>()
            .AddSingleton(new GuildNavEntry("points", "Points", GuildNavGroup.Community, 20, Module: PointsEngine.ModuleId));
}
