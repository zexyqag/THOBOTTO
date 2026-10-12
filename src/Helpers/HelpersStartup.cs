using THOBOTTO.Panel;

namespace THOBOTTO.Helpers;

public static class HelpersStartup
{
    public static IServiceCollection AddHelpers(this IServiceCollection services)
    {
        services.AddOptions<HelpersOptions>().BindConfiguration("Helpers");
        services.AddOptions<LavalinkOptions>().BindConfiguration("Lavalink");
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
            .AddSingleton<YoutubeSignIn>();
    }

    public static WebApplication MapHelpers(this WebApplication app)
    {
        app.MapGet("/g/{guildId}/helpers/{id}/download", (HttpContext context, ulong guildId, long id, PanelAccess access, PersonalityBook book)
            => PersonalityDownload.ServeAsync(context, guildId, id, access, book)).RequireAuthorization();
        return app;
    }
}
