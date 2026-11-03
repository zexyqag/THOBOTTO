using THOBOTTO.Modules;

namespace THOBOTTO.Reposts;

public static class RepostsStartup
{
    public static IServiceCollection AddReposts(this IServiceCollection services)
    {
        services.AddHttpClient(nameof(Reposter), c => c.Timeout = TimeSpan.FromMinutes(1)).AddAsKeyed(ServiceLifetime.Singleton);
        return services
            .AddSingleton<Reposter>()
            .AddSingleton<PostingPreferences>()
            .AddSingleton<ISettingsSummary, RepostSummary>();
    }
}
