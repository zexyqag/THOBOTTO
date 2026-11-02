using THOBOTTO.Modules;

namespace THOBOTTO.Happenings;

public static class HappeningsStartup
{
    public static IServiceCollection AddHappenings(this IServiceCollection services)
        => services
            .AddSingleton<IPostingSource, HappeningPostings>()
            .AddSingleton<HappeningForum>()
            .AddHostedService<HappeningSweep>();
}
