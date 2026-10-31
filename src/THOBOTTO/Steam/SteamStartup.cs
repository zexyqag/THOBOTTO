using Microsoft.AspNetCore.Authentication.Cookies;

using THOBOTTO.Events;

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
        services.AddAuthentication()
            .AddCookie(SteamSignIn.LinkScheme, o =>
            {
                o.Cookie.Name = "thobotto-steam";
                o.ExpireTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddSteam(o =>
            {
                o.SignInScheme = SteamSignIn.LinkScheme;
                // Steam's answer comes back as a top-level GET, which Lax cookies come with; dev's panel is plain http.
                o.CorrelationCookie.SameSite = SameSiteMode.Lax;
                o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.Events.OnRemoteFailure = SteamSignIn.FailedAsync;
            });
        return services
            .AddSingleton<SteamClient>()
            .AddSingleton<SteamLibrary>()
            .AddSingleton<IEventDecorator, SteamSessionLines>()
            .AddHostedService(s => s.GetRequiredService<SteamLibrary>());
    }

    public static WebApplication MapSteam(this WebApplication app)
    {
        app.MapGet("/steam/connect", (HttpContext context, string? back) => SteamSignIn.Connect(context, back)).RequireAuthorization();
        app.MapGet("/steam/linked", (HttpContext context, SteamLibrary library, ILogger<SteamClient> logger) => SteamSignIn.LinkedAsync(context, library, logger)).RequireAuthorization();
        return app;
    }
}
