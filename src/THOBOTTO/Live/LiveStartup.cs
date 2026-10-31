using THOBOTTO.Members;
using THOBOTTO.Modules;
using THOBOTTO.Overview;

namespace THOBOTTO.Live;

public static class LiveStartup
{
    public static IServiceCollection AddLive(this IServiceCollection services)
        => services
            .AddSingleton<LiveWatch>()
            .AddSingleton<TwitchClient>()
            .AddSingleton<TwitchWatch>()
            .AddHostedService(s => s.GetRequiredService<TwitchWatch>())
            .AddSingleton<IWidgetKind, LiveNowWidget>()
            .AddSingleton<ISettingsSummary, LiveSummary>()
            .AddSingleton<IMemberData, LiveMemberData>()
            .AddHostedService(s => s.GetRequiredService<LiveWatch>());
}
