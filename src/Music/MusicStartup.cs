using THOBOTTO.Archive;
using THOBOTTO.Helpers;
using THOBOTTO.Members;
using THOBOTTO.Overview;
using THOBOTTO.Panel;
using THOBOTTO.Voice.Commands;
using THOBOTTO.Voice.Speaking;

namespace THOBOTTO.Music;

public static class MusicStartup
{
    public static IServiceCollection AddMusic(this IServiceCollection services)
    {
        services.AddHttpClient(nameof(LastfmClient), c =>
        {
            c.BaseAddress = new("https://ws.audioscrobbler.com/2.0/");
            c.Timeout = TimeSpan.FromSeconds(10);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("THOBOTTO (Discord bot)");
        }).AddAsKeyed(ServiceLifetime.Singleton);
        services.AddHttpClient(nameof(LyricsFinder), c =>
        {
            c.BaseAddress = new("https://lrclib.net/api/");
            c.Timeout = TimeSpan.FromSeconds(10);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("THOBOTTO (Discord bot)");
        }).AddAsKeyed(ServiceLifetime.Singleton);
        return services
            .AddSingleton<MusicService>()
            .AddSingleton<IWidgetKind, NowPlayingWidget>()
            .AddSingleton<IHelperAware>(s => s.GetRequiredService<MusicService>())
            .AddSingleton<IHelperWork>(s => s.GetRequiredService<MusicService>())
            .AddSingleton<LyricsFinder>()
            .AddSingleton<PlaylistBook>()
            .AddSingleton<LastfmClient>()
            .AddSingleton<Scrobbler>()
            .AddSingleton<BlendMaker>()
            .AddSingleton<ListenTracker>()
            .AddSingleton<PlayHistory>()
            .AddSingleton<IExportSource, PlaysExport>()
            .AddHostedService<MusicAutoJoin>()
            // Started after the helpers, so it stops before them and saves what they still play.
            .AddHostedService(s => s.GetRequiredService<MusicService>())
            .AddSingleton<IVoiceActions, MusicVoiceActions>()
            .AddSingleton<IMemberData, PlaysMemberData>()
            .AddSingleton(new GuildNavEntry("music", "Music", GuildNavGroup.Community, 30, Module: MusicService.ModuleId));
    }

    public static WebApplication MapMusic(this WebApplication app)
    {
        app.MapGet("/lastfm/connect", (HttpContext context, string? back, LastfmClient client) => LastfmLinking.Connect(context, back, client)).RequireAuthorization();
        app.MapGet("/lastfm/callback", (HttpContext context, string? token, LastfmClient client, Scrobbler scrobbler, ILogger<LastfmClient> logger)
            => LastfmLinking.CallbackAsync(context, token, client, scrobbler, logger)).RequireAuthorization();
        return app;
    }
}
