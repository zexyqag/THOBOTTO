namespace THOBOTTO.Moderation;

public static class ModerationStartup
{
    public static IServiceCollection AddModeration(this IServiceCollection services)
        => services
            .AddSingleton<CaseBook>()
            .AddSingleton<ModActions>()
            .AddSingleton<BanAppeals>()
            .AddSingleton<AutoModSetup>()
            .AddHostedService<ModTimers>();
}
