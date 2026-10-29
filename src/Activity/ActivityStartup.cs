using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.FileProviders;

using NetCord;
using NetCord.Gateway;

using THOBOTTO.Activity.Watch;
using THOBOTTO.Debugging;
using THOBOTTO.Discord;
using THOBOTTO.Integrations;
using THOBOTTO.Panel;

namespace THOBOTTO.Activity;

// The Activity (Discord shows it in the voice channel): its page, signing in through Discord, the live
// connection, and cover art passed through (an Activity may only load from the addresses Discord maps).
public static class ActivityStartup
{
    // Where cover art comes from; nothing else is passed through.
    private static readonly string[] ArtHosts = ["i.ytimg.com", "i.scdn.co", "i1.sndcdn.com", "lh3.googleusercontent.com", "yt3.ggpht.com", "is1-ssl.mzstatic.com", "resources.tidal.com", "e-cdns-images.dzcdn.net"];

    // Blob media and worker for the video player; no frame-ancestors, as Discord frames the page.
    private const string PagePolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; "
        + "media-src 'self' blob:; worker-src 'self' blob:; connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'";

    public static IServiceCollection AddActivity(this IServiceCollection services)
    {
        services.AddHttpClient("activity", c => c.Timeout = TimeSpan.FromSeconds(15));
        return services
            .AddSingleton<ActivitySessions>()
            .AddSingleton<ActivityHub>()
            .AddSingleton<ActivityPolls>()
            .AddHostedService(s => s.GetRequiredService<ActivityPolls>());
    }

    public static WebApplication MapActivity(this WebApplication app)
    {
        app.UseWebSockets();
        // With the slash, so the page's own files ("./assets/…") are found.
        app.MapGet("/activity/", (HttpContext context, IWebHostEnvironment env) =>
        {
            if (!context.Request.Path.Value!.EndsWith('/'))
                return Results.Redirect("/activity/" + context.Request.QueryString);
            if (env.WebRootFileProvider.GetFileInfo("activity/index.html") is not { Exists: true } page)
                return Results.Text("The Activity isn't built into this image.");
            context.Response.Headers.ContentSecurityPolicy = PagePolicy;
            return Results.Stream(page.CreateReadStream(), "text/html");
        });

        app.MapGet("/activity/api/config", async (IConfiguration config, PanelLinks links) =>
            Results.Json(new { clientId = new BotToken(config["Discord:Token"]!).Id.ToString(), panel = await links.AddressAsync() }));

        // In a browser (not inside Discord): signed in to the panel, following the voice channel they're in.
        app.MapGet("/activity/api/web-session", (HttpContext context, ActivitySessions sessions, VoicePresence presence, GatewayClient gateway) =>
        {
            if (PanelAccess.UserId(context.User) is not { } userId)
                return Results.Unauthorized();
            var here = gateway.Cache.Guilds.Values
                .Select(g => presence.Snapshot(g.Id).TryGetValue(userId, out var p) ? new { guild = g.Id.ToString(), channel = p.ChannelId.ToString() } : null)
                .FirstOrDefault(v => v is not null);
            return Results.Json(new { session = sessions.Issue(userId), voice = here });
        });

        // The code from Discord's authorize, for an access token (for the page's SDK) and a session (for us).
        app.MapPost("/activity/api/token", async (CodeRequest request, IConfiguration config, IntegrationStore store, ActivitySessions sessions, IHttpClientFactory clients) =>
        {
            if (store.Get(IntegrationStore.PanelClientSecret) is not { } secret)
                return Results.Problem("The client secret isn't set on the panel's Integrations page.", statusCode: 503);
            var http = clients.CreateClient("activity");
            using var exchange = await http.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = new BotToken(config["Discord:Token"]!).Id.ToString(),
                ["client_secret"] = secret,
                ["grant_type"] = "authorization_code",
                ["code"] = request.Code,
            }));
            if (!exchange.IsSuccessStatusCode)
                return Results.Problem("Discord didn't accept the sign-in.", statusCode: 401);
            var accessToken = (await exchange.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
            using var me = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/users/@me");
            me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var user = await http.SendAsync(me);
            var id = ulong.Parse((await user.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
            return Results.Json(new { accessToken, session = sessions.Issue(id) });
        });

        // Development: the page outside Discord, as anyone.
        if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Panel:DevLogin"))
            app.MapGet("/activity/api/dev-session", (ulong user, ActivitySessions sessions) => Results.Json(new { session = sessions.Issue(user) }));

        app.MapGet("/activity/api/art", async (string url, HttpContext context, IHttpClientFactory clients) =>
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !ArtHosts.Contains(uri.Host))
                return Results.NotFound();
            using var response = await clients.CreateClient("activity").GetAsync(uri);
            if (!response.IsSuccessStatusCode)
                return Results.NotFound();
            context.Response.Headers.CacheControl = "public, max-age=86400";
            return Results.Bytes(await response.Content.ReadAsByteArrayAsync(), response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
        });

        // A video's stream: its address (random) is all the access it needs, as players fetch it without headers.
        app.MapGet("/activity/api/watch/{id}/{file}", async (string id, string file, HttpContext context, HlsStreams streams, Telemetry telemetry) =>
        {
            if (await streams.FileOfAsync(id, file, context.RequestAborted) is not { } path)
                return Results.NotFound();
            telemetry.VideoSent(new FileInfo(path).Length);
            context.Response.Headers.CacheControl = file.EndsWith(".m3u8") ? "no-cache" : "public, max-age=3600";
            return Results.File(path, file.EndsWith(".m3u8") ? "application/vnd.apple.mpegurl" : file.EndsWith(".m4s") ? "video/iso.segment" : "video/mp4");
        });

        app.Map("/activity/api/live", async (HttpContext context, string session, ulong guild, ulong channel, ActivitySessions sessions, PanelAccess access, ActivityHub hub, Telemetry telemetry) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
                return Results.BadRequest();
            if (sessions.UserOf(session) is not { } userId || await access.InAsync(guild, userId) is not var (found, member) || !ActivityHub.MayWatch(found, member, channel))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var open = telemetry.Opened(channel);
            await hub.RunAsync(socket, found, member, channel, context.RequestAborted);
            return Results.Empty;
        });
        return app;
    }

    public sealed record CodeRequest(string Code);
}
