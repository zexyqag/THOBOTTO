using THOBOTTO.Panel;

namespace THOBOTTO.Steam;

public static class SteamStartup
{
    public static IServiceCollection AddSteam(this IServiceCollection services)
    {
        services.AddHttpClient(nameof(SteamClient), c =>
            {
                c.Timeout = TimeSpan.FromSeconds(20);
                c.DefaultRequestHeaders.UserAgent.ParseAdd("THOBOTTO (Discord bot)");
            })
            .AddAsKeyed(ServiceLifetime.Singleton);
        return services
            .AddSingleton<SteamClient>()
            .AddSingleton<SteamLibrary>()
            .AddHostedService(s => s.GetRequiredService<SteamLibrary>());
    }

    public static WebApplication MapSteam(this WebApplication app)
    {
        app.MapGet("/steam/connect", (HttpContext context, string? back, PanelLinks links) => SteamSignIn.ConnectAsync(context, back, links)).RequireAuthorization();
        app.MapGet("/steam/callback", (HttpContext context, PanelLinks links, SteamClient client, SteamLibrary library, ILogger<SteamClient> logger)
            => SteamSignIn.CallbackAsync(context, links, client, library, logger)).RequireAuthorization();
        return app;
    }
}
