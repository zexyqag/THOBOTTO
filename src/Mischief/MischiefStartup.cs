using THOBOTTO.Voice.Commands;

namespace THOBOTTO.Mischief;

public static class MischiefStartup
{
    public static IServiceCollection AddMischief(this IServiceCollection services)
        => services
            .AddSingleton<IVoiceActions, MischiefVoiceActions>()
            .AddSingleton<PaintRoles>()
            .AddSingleton<MischiefActions>()
            .AddHostedService(s => s.GetRequiredService<PaintRoles>());
}
