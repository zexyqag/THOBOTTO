using THOBOTTO.Members;
using THOBOTTO.Modules;

namespace THOBOTTO.Reposts;

public static class RepostsStartup
{
    public static IServiceCollection AddReposts(this IServiceCollection services)
    {
        services.AddHttpClient(nameof(Reposter), c => c.Timeout = TimeSpan.FromMinutes(1)).AddAsKeyed(ServiceLifetime.Singleton);
        services.AddHttpClient(nameof(LaterPosts), c => c.Timeout = TimeSpan.FromMinutes(1)).AddAsKeyed(ServiceLifetime.Singleton);
        return services
            .AddSingleton<Reposter>()
            .AddSingleton<LaterPosts>()
            .AddHostedService(s => s.GetRequiredService<LaterPosts>())
            .AddSingleton<IMemberData, LaterPostsData>()
            .AddSingleton<PostingPreferences>()
            .AddSingleton<ISettingsSummary, RepostSummary>();
    }
}
