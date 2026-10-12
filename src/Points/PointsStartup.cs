using THOBOTTO.Voice.Commands;

namespace THOBOTTO.Points;

public static class PointsStartup
{
    public static IServiceCollection AddPoints(this IServiceCollection services)
        => services
            .AddSingleton<PointsEngine>()
            .AddHostedService(s => s.GetRequiredService<PointsEngine>())
            .AddSingleton<IVoiceActions, KudosVoiceActions>()
            .AddSingleton<IVoiceActions, PointsVoiceActions>();
}
