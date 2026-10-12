using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;

using NetCord.Rest;

using THOBOTTO.Integrations;
using THOBOTTO.Panel.Layout;

namespace THOBOTTO.Panel;

public static class PanelStartup
{
    private static bool DevLogin(IConfiguration config, IHostEnvironment env) => env.IsDevelopment() && config.GetValue<bool>("Panel:DevLogin");

    public static IServiceCollection AddPanel(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddHttpClient("panel", c => c.Timeout = TimeSpan.FromSeconds(15));
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
        return services;
    }

    // The middleware every endpoint mapped after this runs through, and the panel's own endpoints.
    public static WebApplication MapPanel(this WebApplication app)
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

        app.MapGet("/login", (HttpContext context, IConfiguration config, IntegrationStore store, PanelLinks links, string? back) => DiscordLogin.StartAsync(context, config, store, links, back));
        app.MapGet(PanelLinks.Callback, (HttpContext context, string? code, string? state, IConfiguration config, IntegrationStore store, PanelLinks links, IHttpClientFactory clients, ILogger<PanelLinks> logger)
            => DiscordLogin.CallbackAsync(context, code, state, config, store, links, clients, logger));
        app.MapGet("/login/link", (HttpContext context, string? token, PanelLinks links) => DiscordLogin.LinkAsync(context, token, links));
        app.MapPost("/logout", async (HttpContext context) =>
        {
            context.Response.Cookies.Delete(PanelAccess.PreviewCookie);
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/");
        });
        app.MapGet("/layout", (HttpContext context, bool slim, string? back) =>
        {
            context.Response.Cookies.Append(MainLayout.Cookie, slim ? "true" : "false", new() { MaxAge = TimeSpan.FromDays(365), SameSite = SameSiteMode.Lax, Secure = context.Request.IsHttps });
            // Only back to a page of the panel itself.
            return Results.Redirect(back is { Length: > 0 } && back.StartsWith('/') && !back.StartsWith("//") && !back.StartsWith("/\\") ? back : "/");
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

        app.MapRazorComponents<App>();
        return app;
    }
}
