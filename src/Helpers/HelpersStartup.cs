using THOBOTTO.Access;
using THOBOTTO.Panel;

namespace THOBOTTO.Helpers;

public static class HelpersStartup
{
    public static IServiceCollection AddHelpers(this IServiceCollection services)
    {
        services.AddOptions<HelpersOptions>().BindConfiguration("Helpers");
        services.AddOptions<LavalinkOptions>().BindConfiguration("Lavalink");
        // Every player operation waits on Lavalink's answer, so each call sets its own time; the import download has one.
        services.AddHttpClient(LavalinkClient.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient("helpers", c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddHttpClient(nameof(YoutubeSignIn), c =>
        {
            c.BaseAddress = new("https://www.youtube.com/o/oauth2/");
            c.Timeout = TimeSpan.FromSeconds(15);
        }).AddAsKeyed(ServiceLifetime.Singleton);
        return services
            .AddSingleton<HelperFleet>()
            .AddHostedService(s => s.GetRequiredService<HelperFleet>())
            // Attached to each helper first: the personality it wears comes before what it does.
            .AddSingleton<PersonalityBook>()
            .AddSingleton<IHelperAware>(s => s.GetRequiredService<PersonalityBook>())
            .AddSingleton<ListeningSeats>()
            .AddSingleton<VoicePrefs>()
            .AddSingleton<VoiceMouths>()
            .AddSingleton<LavalinkSetup>()
            .AddSingleton<IHelperAware>(s => s.GetRequiredService<LavalinkSetup>())
            .AddSingleton<YoutubeSignIn>()
            .AddSingleton(new BotNavEntry("/helpers", "Helper bots", 30))
            .AddSingleton(new GuildNavEntry("helpers", "Helpers", 80, BotPermissions.ManageHelpers));
    }

    public static WebApplication MapHelpers(this WebApplication app)
    {
        app.MapGet("/g/{guildId}/helpers/{id}/download", (HttpContext context, ulong guildId, long id, PanelAccess access, PersonalityBook book)
            => PersonalityDownload.ServeAsync(context, guildId, id, access, book)).RequireAuthorization();
        return app;
    }
}
