using THOBOTTO.Voice.Commands;

namespace THOBOTTO.Mischief.Bets;

public static class BetsStartup
{
    public static IServiceCollection AddBets(this IServiceCollection services)
        => services
            .AddSingleton<IVoiceActions, BetVoiceActions>()
            .AddSingleton<BetBook>()
            .AddHostedService(s => s.GetRequiredService<BetBook>());
}
