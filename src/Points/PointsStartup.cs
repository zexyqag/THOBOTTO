using THOBOTTO.Archive;
using THOBOTTO.Panel;
using THOBOTTO.Voice.Commands;

namespace THOBOTTO.Points;

public static class PointsStartup
{
    public static IServiceCollection AddPoints(this IServiceCollection services)
        => services
            .AddSingleton<PointsEngine>()
            .AddHostedService(s => s.GetRequiredService<PointsEngine>())
            .AddSingleton<IVoiceActions, KudosVoiceActions>()
            .AddSingleton<IVoiceActions, PointsVoiceActions>()
            .AddSingleton<IExportSource, PointsExport>()
            .AddSingleton(new GuildNavEntry("points", "Points", GuildNavGroup.Community, 20, Module: PointsEngine.ModuleId));
}
