using THOBOTTO.Archive;
using THOBOTTO.Voice.Commands;

namespace THOBOTTO.Mischief.Bets;

public static class BetsStartup
{
    public static IServiceCollection AddBets(this IServiceCollection services)
        => services
            .AddSingleton<IVoiceActions, BetVoiceActions>()
            .AddSingleton<IExportSource, BetsExport>()
            .AddSingleton<BetBook>()
            .AddHostedService(s => s.GetRequiredService<BetBook>());
}
