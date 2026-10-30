using THOBOTTO.Modules;

namespace THOBOTTO.Watch;

public static class WatchStartup
{
    public static IServiceCollection AddWatch(this IServiceCollection services)
    {
        services.AddHttpClient(nameof(YoutubeCookies), c => c.Timeout = TimeSpan.FromSeconds(15)).AddAsKeyed(ServiceLifetime.Singleton);
        return services
            .AddSingleton<YoutubeCookies>()
            .AddHostedService(s => s.GetRequiredService<YoutubeCookies>())
            .AddSingleton<VideoResolver>()
            .AddSingleton<HlsStreams>()
            .AddSingleton<WatchRooms>()
            .AddSingleton<IPriceSource, WatchPrices>()
            .AddHostedService(s => s.GetRequiredService<WatchRooms>());
    }
}
