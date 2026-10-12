namespace THOBOTTO.Activity.Watch;

public static class WatchStartup
{
    public static IServiceCollection AddWatch(this IServiceCollection services)
        => services
            .AddSingleton<YoutubeCookies>()
            .AddHostedService(s => s.GetRequiredService<YoutubeCookies>())
            .AddSingleton<VideoResolver>()
            .AddSingleton<HlsStreams>()
            .AddSingleton<WatchRooms>()
            .AddHostedService(s => s.GetRequiredService<WatchRooms>());
}
