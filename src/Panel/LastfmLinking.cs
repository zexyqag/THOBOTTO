using THOBOTTO.Lastfm;

namespace THOBOTTO.Panel;

// Linking a member's Last.fm account: off to Last.fm to approve, back with a token, swapped for a session.
public static class LastfmLinking
{
    private const string BackCookie = "lastfm-back";

    public static IResult Connect(HttpContext context, string? back, LastfmClient client)
    {
        if (!client.Configured)
            return Results.NotFound();
        // Only pages of the panel itself, so the link can't send anyone elsewhere.
        context.Response.Cookies.Append(BackCookie, Local(back), new() { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(15) });
        return Results.Redirect(client.AuthorizeUrl($"{context.Request.Scheme}://{context.Request.Host}/lastfm/callback"));
    }

    public static async Task<IResult> CallbackAsync(HttpContext context, string? token, LastfmClient client, Scrobbler scrobbler, ILogger<LastfmClient> logger)
    {
        var back = Local(context.Request.Cookies[BackCookie]);
        context.Response.Cookies.Delete(BackCookie);
        if (PanelAccess.UserId(context.User) is not { } userId || string.IsNullOrEmpty(token) || !client.Configured)
            return Results.Redirect($"{back}?lastfm=failed");
        try
        {
            var (username, sessionKey) = await client.GetSessionAsync(token);
            await scrobbler.LinkAsync(userId, username, sessionKey);
            return Results.Redirect($"{back}?lastfm=linked");
        }
        catch (Exception ex) when (ex is LastfmException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            logger.LogWarning("Linking Last.fm for {UserId} failed: {Message}", userId, ex.Message);
            return Results.Redirect($"{back}?lastfm=failed");
        }
    }

    private static string Local(string? path) => path is { Length: > 1 } p && p[0] == '/' && p[1] is not ('/' or '\\') ? p : "/";
}
