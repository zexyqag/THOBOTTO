using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;

using THOBOTTO.Archive;
using THOBOTTO.Data;
using THOBOTTO.Helpers;
using THOBOTTO.Lastfm;
using THOBOTTO.Panel.Components;

namespace THOBOTTO.Panel;

public static class PanelSetup
{
    private const string Discord = "Discord";

    public static bool Enabled(IConfiguration config, IHostEnvironment env)
        => !string.IsNullOrEmpty(config["Panel:ClientSecret"]) || DevLogin(config, env);

    private static bool DevLogin(IConfiguration config, IHostEnvironment env) => env.IsDevelopment() && config.GetValue<bool>("Panel:DevLogin");

    public static void AddPanel(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        if (!Enabled(config, env))
            return;

        services.AddOptions<PanelOptions>().BindConfiguration("Panel");
        services.AddSingleton<PanelAccess>();
        services.AddSingleton<SettingsPages>();
        services.AddSingleton<PanelNames>();
        services.AddRazorComponents();
        services.AddCascadingAuthenticationState();
        services.AddAuthorization();
        // Behind Nginx Proxy Manager: trust its X-Forwarded-* so links and the login redirect use https.
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
        });

        var auth = services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "thobotto";
                o.LoginPath = "/login";
                o.ExpireTimeSpan = TimeSpan.FromDays(30);
                o.SlidingExpiration = true;
            });
        if (config["Panel:ClientSecret"] is { Length: > 0 } secret)
        {
            auth.AddOAuth(Discord, o =>
            {
                // The bot's application id is its user id, which its token starts with.
                o.ClientId = new BotToken(config["Discord:Token"]!).Id.ToString();
                o.ClientSecret = secret;
                o.AuthorizationEndpoint = "https://discord.com/oauth2/authorize";
                o.TokenEndpoint = "https://discord.com/api/oauth2/token";
                o.UserInformationEndpoint = "https://discord.com/api/users/@me";
                o.CallbackPath = "/signin-discord";
                o.Scope.Add("identify");
                o.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
                o.ClaimActions.MapJsonKey(ClaimTypes.Name, "global_name");
                o.Events.OnCreatingTicket = async context =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
                    using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
                    response.EnsureSuccessStatusCode();
                    using var user = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
                    context.RunClaimActions(user.RootElement);
                    // Without a display name, the username.
                    if (context.Identity!.FindFirst(ClaimTypes.Name) is null && user.RootElement.TryGetProperty("username", out var username))
                        context.Identity.AddClaim(new(ClaimTypes.Name, username.GetString()!));
                };
            });
        }
    }

    public static void MapPanel(this WebApplication app)
    {
        if (!Enabled(app.Configuration, app.Environment))
            return;

        app.UseForwardedHeaders();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapStaticAssets();

        app.MapGet("/login", (HttpContext context) => !string.IsNullOrEmpty(app.Configuration["Panel:ClientSecret"])
            ? Results.Challenge(new() { RedirectUri = "/" }, [Discord])
            : Results.Text("Discord login isn't configured (Panel:ClientSecret)."));
        app.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/");
        });

        if (DevLogin(app.Configuration, app.Environment))
        {
            app.MapGet("/dev-login", async (HttpContext context, ulong user, RestClient rest) =>
            {
                var found = await rest.GetUserAsync(user);
                var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, user.ToString()), new(ClaimTypes.Name, found.GlobalName ?? found.Username)], CookieAuthenticationDefaults.AuthenticationScheme);
                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new(identity));
                return Results.Redirect("/");
            });
        }

        app.MapGet("/g/{guildId}/archive/file/{id}", (HttpContext context, ulong guildId, ulong id, bool? download, PanelAccess access, IDbContextFactory<BotDbContext> dbFactory, IAttachmentStore store)
            => ArchiveFiles.ServeAsync(context, guildId, id, download == true, access, dbFactory, store)).RequireAuthorization();
        app.MapGet("/g/{guildId}/helpers/{id}/download", (HttpContext context, ulong guildId, long id, PanelAccess access, PersonalityBook book)
            => PersonalityDownload.ServeAsync(context, guildId, id, access, book)).RequireAuthorization();
        app.MapGet("/lastfm/connect", (HttpContext context, string? back, LastfmClient client) => LastfmLinking.Connect(context, back, client)).RequireAuthorization();
        app.MapGet("/lastfm/callback", (HttpContext context, string? token, LastfmClient client, Scrobbler scrobbler, ILogger<LastfmClient> logger)
            => LastfmLinking.CallbackAsync(context, token, client, scrobbler, logger)).RequireAuthorization();
        app.MapRazorComponents<App>();
    }
}
