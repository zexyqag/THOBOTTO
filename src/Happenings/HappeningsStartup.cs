namespace THOBOTTO.Happenings;

public static class HappeningsStartup
{
    public static IServiceCollection AddHappenings(this IServiceCollection services)
        => services
            .AddSingleton<HappeningForum>()
            .AddHostedService<HappeningSweep>();
}
