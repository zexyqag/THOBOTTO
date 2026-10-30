using THOBOTTO.Panel;

namespace THOBOTTO.Feeds;

public static class FeedsStartup
{
    public static IServiceCollection AddFeeds(this IServiceCollection services)
    {
        services.AddHttpClient(nameof(FeedFetcher), c =>
            {
                c.Timeout = TimeSpan.FromSeconds(20);
                c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; THOBOTTO news feeds)");
            })
            .ConfigurePrimaryHttpMessageHandler(FeedFetcher.Handler)
            .AddAsKeyed(ServiceLifetime.Singleton);
        return services
            .AddSingleton<FeedFetcher>()
            .AddSingleton<FeedWatch>()
            .AddSingleton(new GuildNavEntry("feeds", "News feeds", GuildNavGroup.Community, 75, Module: FeedWatch.ModuleId))
            .AddHostedService(s => s.GetRequiredService<FeedWatch>());
    }
}
