using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.FileProviders;

using NetCord;

using THOBOTTO.Integrations;
using THOBOTTO.Panel;

namespace THOBOTTO.Activity;

// The Activity (Discord shows it in the voice channel): its page, signing in through Discord, the live
// connection, and cover art passed through (an Activity may only load from the addresses Discord maps).
public static class ActivityEndpoints
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // Where cover art comes from; nothing else is passed through.
    private static readonly string[] ArtHosts = ["i.ytimg.com", "i.scdn.co", "i1.sndcdn.com", "lh3.googleusercontent.com", "yt3.ggpht.com", "is1-ssl.mzstatic.com", "resources.tidal.com", "e-cdns-images.dzcdn.net"];

    public static void MapActivity(this WebApplication app)
    {
        app.UseWebSockets();
        // With the slash, so the page's own files ("./assets/…") are found.
        app.MapGet("/activity/", (HttpContext context, IWebHostEnvironment env) =>
            !context.Request.Path.Value!.EndsWith('/') ? Results.Redirect("/activity/" + context.Request.QueryString)
            : env.WebRootFileProvider.GetFileInfo("activity/index.html") is { Exists: true } page ? Results.Stream(page.CreateReadStream(), "text/html")
            : Results.Text("The Activity isn't built into this image."));

        app.MapGet("/activity/api/config", (IConfiguration config) => Results.Json(new { clientId = new BotToken(config["Discord:Token"]!).Id.ToString() }));

        // The code from Discord's authorize, for an access token (for the page's SDK) and a session (for us).
        app.MapPost("/activity/api/token", async (CodeRequest request, IConfiguration config, IntegrationStore store, ActivitySessions sessions) =>
        {
            if (store.Get(IntegrationStore.PanelClientSecret) is not { } secret)
                return Results.Problem("The client secret isn't set on the panel's Integrations page.", statusCode: 503);
            using var exchange = await Http.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
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
            using var user = await Http.SendAsync(me);
            var id = ulong.Parse((await user.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
            return Results.Json(new { accessToken, session = sessions.Issue(id) });
        });

        // Development: the page outside Discord, as anyone.
        if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Panel:DevLogin"))
            app.MapGet("/activity/api/dev-session", (ulong user, ActivitySessions sessions) => Results.Json(new { session = sessions.Issue(user) }));

        app.MapGet("/activity/api/art", async (string url, HttpContext context) =>
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !ArtHosts.Contains(uri.Host))
                return Results.NotFound();
            using var response = await Http.GetAsync(uri);
            if (!response.IsSuccessStatusCode)
                return Results.NotFound();
            context.Response.Headers.CacheControl = "public, max-age=86400";
            return Results.Bytes(await response.Content.ReadAsByteArrayAsync(), response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
        });

        // A video's stream: its address (random) is all the access it needs, as players fetch it without headers.
        app.MapGet("/activity/api/watch/{id}/{file}", async (string id, string file, HttpContext context, THOBOTTO.Watch.HlsStreams streams) =>
        {
            if (await streams.FileOfAsync(id, file, context.RequestAborted) is not { } path)
                return Results.NotFound();
            context.Response.Headers.CacheControl = file.EndsWith(".m3u8") ? "no-cache" : "public, max-age=3600";
            return Results.File(path, file.EndsWith(".m3u8") ? "application/vnd.apple.mpegurl" : file.EndsWith(".m4s") ? "video/iso.segment" : "video/mp4");
        });

        app.Map("/activity/api/live", async (HttpContext context, string session, ulong guild, ulong channel, ActivitySessions sessions, PanelAccess access, ActivityHub hub) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
                return Results.BadRequest();
            if (sessions.UserOf(session) is not { } userId || await access.InAsync(guild, userId) is not var (found, member))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await hub.RunAsync(socket, found, member, channel, context.RequestAborted);
            return Results.Empty;
        });
    }

    public sealed record CodeRequest(string Code);
}
