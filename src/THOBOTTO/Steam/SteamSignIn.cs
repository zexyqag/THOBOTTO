using System.Security.Cryptography;
using System.Text.Json;

using THOBOTTO.Panel;

namespace THOBOTTO.Steam;

// Linking a member's Steam account: off to sign in through Steam, back with who they are, which Steam confirms. The
// state in a cookie and in the signed return address keeps someone else's sign-in from being linked to them.
public static class SteamSignIn
{
    private const string StateCookie = "steam-state";
    private const string BackCookie = "steam-back";

    public static async Task<IResult> ConnectAsync(HttpContext context, string? back, PanelLinks links)
    {
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var cookie = new CookieOptions { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(15) };
        context.Response.Cookies.Append(StateCookie, state, cookie);
        context.Response.Cookies.Append(BackCookie, PanelPath.Local(back) ?? "/", cookie);
        var address = await AddressAsync(context, links);
        return Results.Redirect(SteamClient.SignInUrl(ReturnTo(address, state), address));
    }

    public static async Task<IResult> CallbackAsync(HttpContext context, PanelLinks links, SteamClient client, SteamLibrary library, ILogger<SteamClient> logger)
    {
        var back = PanelPath.Local(context.Request.Cookies[BackCookie]) ?? "/";
        var state = context.Request.Cookies[StateCookie];
        context.Response.Cookies.Delete(BackCookie);
        context.Response.Cookies.Delete(StateCookie);
        if (PanelAccess.UserId(context.User) is not { } userId || state is null || context.Request.Query["state"] != state)
            return Results.Redirect($"{back}?steam=failed");
        try
        {
            var answer = context.Request.Query.Select(q => KeyValuePair.Create(q.Key, q.Value.ToString()));
            if (await client.VerifyAsync(answer, ReturnTo(await AddressAsync(context, links), state)) is not { } steamId)
                return Results.Redirect($"{back}?steam=failed");
            var problem = await library.LinkAsync(userId, steamId);
            return Results.Redirect($"{back}?steam={(problem is null ? "linked" : "unread")}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("Linking Steam for {UserId} failed: {Message}", userId, ex.Message);
            return Results.Redirect($"{back}?steam=failed");
        }
    }

    private static string ReturnTo(string address, string state) => $"{address}/steam/callback?state={state}";

    // The panel's own address where it has one, as the request may have come through a proxy.
    private static async Task<string> AddressAsync(HttpContext context, PanelLinks links)
        => await links.AddressAsync() ?? $"{context.Request.Scheme}://{context.Request.Host}";
}
