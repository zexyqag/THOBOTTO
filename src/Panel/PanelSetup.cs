
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;

using THOBOTTO.Archive;
using THOBOTTO.Backups;
using THOBOTTO.Data;
using THOBOTTO.Helpers;
using THOBOTTO.Integrations;
using THOBOTTO.Lastfm;
using THOBOTTO.Panel.Components;

namespace THOBOTTO.Panel;

public static class PanelSetup
{
    private static bool DevLogin(IConfiguration config, IHostEnvironment env) => env.IsDevelopment() && config.GetValue<bool>("Panel:DevLogin");

    public static void AddPanel(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<PanelAccess>();
        services.AddSingleton<PanelNames>();
        services.AddSingleton<PanelLinks>();
        services.AddRazorComponents();
        services.AddCascadingAuthenticationState();
        services.AddAuthorization();
        // Behind Nginx Proxy Manager: trust its X-Forwarded-* so links and cookies use https.
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
        });

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "thobotto";
                o.LoginPath = "/";
                o.ExpireTimeSpan = TimeSpan.FromDays(30);
                o.SlidingExpiration = true;
            });
    }

    public static void MapPanel(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseAuthentication();
        app.UseAuthorization();
        // Viewing as a role changes nothing.
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsPost(context.Request.Method) && context.Request.Cookies.ContainsKey(PanelAccess.PreviewCookie) && context.Request.Path != "/logout")
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsync("You're viewing the panel as a role, so nothing can be changed. Stop viewing as it (at the top of the page) first.");
                return;
            }
            await next();
        });
        app.UseAntiforgery();
        app.MapStaticAssets();

        app.MapGet("/login", (HttpContext context, IConfiguration config, IntegrationStore store, PanelLinks links) => DiscordLogin.StartAsync(context, config, store, links));
        app.MapGet(PanelLinks.Callback, (HttpContext context, string? code, string? state, IConfiguration config, IntegrationStore store, PanelLinks links, ILogger<PanelLinks> logger)
            => DiscordLogin.CallbackAsync(context, code, state, config, store, links, logger));
        app.MapGet("/login/link", (HttpContext context, string? token, PanelLinks links) => DiscordLogin.LinkAsync(context, token, links));
        app.MapPost("/logout", async (HttpContext context) =>
        {
            context.Response.Cookies.Delete(PanelAccess.PreviewCookie);
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/");
        });
        app.MapGet("/g/{guildId}/view-as", async (HttpContext context, ulong guildId, ulong? role, PanelAccess access) =>
        {
            if (PanelAccess.UserId(context.User) is not { } userId || await access.InAsync(guildId, userId) is not var (guild, member)
                || !await access.Control.CanAsync(guild, member, PanelAccess.PreviewPermission))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (role is { } chosen && (chosen == guild.Id || guild.Roles.ContainsKey(chosen)))
                context.Response.Cookies.Append(PanelAccess.PreviewCookie, $"{guildId}:{chosen}", new() { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax });
            else
                context.Response.Cookies.Delete(PanelAccess.PreviewCookie);
            return Results.Redirect($"/g/{guildId}");
        }).RequireAuthorization();

        if (DevLogin(app.Configuration, app.Environment))
        {
            app.MapGet("/dev-login", async (HttpContext context, ulong user, RestClient rest) =>
            {
                var found = await rest.GetUserAsync(user);
                await DiscordLogin.SignInAsync(context, user, found.GlobalName ?? found.Username);
                return Results.Redirect("/");
            });
        }

        app.MapGet("/g/{guildId}/archive/file/{id}", (HttpContext context, ulong guildId, ulong id, bool? download, PanelAccess access, IDbContextFactory<BotDbContext> dbFactory, IAttachmentStore store)
            => ArchiveFiles.ServeAsync(context, guildId, id, download == true, access, dbFactory, store)).RequireAuthorization();
        app.MapGet("/g/{guildId}/helpers/{id}/download", (HttpContext context, ulong guildId, long id, PanelAccess access, PersonalityBook book)
            => PersonalityDownload.ServeAsync(context, guildId, id, access, book)).RequireAuthorization();
        app.MapGet("/g/{guildId}/helpers/{id}/voice", (HttpContext context, ulong guildId, long id, string? voice, PanelAccess access, PersonalityBook book, THOBOTTO.Speaking.HelperVoices voices)
            => VoicePreview.ServeAsync(context, guildId, id, voice, access, book, voices)).RequireAuthorization();
        app.MapGet("/lastfm/connect", (HttpContext context, string? back, LastfmClient client) => LastfmLinking.Connect(context, back, client)).RequireAuthorization();
        app.MapGet("/lastfm/callback", (HttpContext context, string? token, LastfmClient client, Scrobbler scrobbler, ILogger<LastfmClient> logger)
            => LastfmLinking.CallbackAsync(context, token, client, scrobbler, logger)).RequireAuthorization();
        app.MapGet("/g/{guildId}/backup/download", (HttpContext context, ulong guildId, long? stored, PanelAccess access, BackupMaker maker, BackupVault vault)
            => BackupDownload.ServeAsync(context, guildId, stored, access, maker, vault)).RequireAuthorization();
        app.MapRazorComponents<App>();
    }
}
