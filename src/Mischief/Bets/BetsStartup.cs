using THOBOTTO.Archive;
using THOBOTTO.Panel;
using THOBOTTO.Voice.Commands;

namespace THOBOTTO.Mischief.Bets;

public static class BetsStartup
{
    public static IServiceCollection AddBets(this IServiceCollection services)
        => services
            .AddSingleton<IVoiceActions, BetVoiceActions>()
            .AddSingleton<IExportSource, BetsExport>()
            .AddSingleton<BetBook>()
            .AddSingleton(new GuildNavEntry("bets", "Bets", GuildNavGroup.Community, 90, Module: MischiefModule.ModuleId))
            .AddHostedService(s => s.GetRequiredService<BetBook>());
}
