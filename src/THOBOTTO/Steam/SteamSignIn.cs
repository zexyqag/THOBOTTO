using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;

using AspNet.Security.OpenId.Steam;

using Microsoft.AspNetCore.Authentication;

using THOBOTTO.Panel;

namespace THOBOTTO.Steam;

// Linking a member's Steam account by signing in through Steam: aspnet-contrib's handler checks Steam's answer (at
// /signin-steam) and keeps who it was in a short cookie of its own, which /steam/linked reads and drops, linking it
// to the member logged in to the panel. Where to go back to is kept in a cookie too: a refused answer comes without
// the sign-in's properties.
public static partial class SteamSignIn
{
    public const string LinkScheme = "SteamLink";
    private const string BackCookie = "steam-back";

    public static IResult Connect(HttpContext context, string? back)
    {
        context.Response.Cookies.Append(BackCookie, PanelPath.Local(back) ?? "/", new() { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(15) });
        return Results.Challenge(new() { RedirectUri = "/steam/linked" }, [SteamAuthenticationDefaults.AuthenticationScheme]);
    }

    public static async Task<IResult> LinkedAsync(HttpContext context, SteamLibrary library, ILogger<SteamClient> logger)
    {
        var signedIn = await context.AuthenticateAsync(LinkScheme);
        await context.SignOutAsync(LinkScheme);
        var back = BackOf(context);
        if (!signedIn.Succeeded || PanelAccess.UserId(context.User) is not { } userId || SteamId(signedIn.Principal?.FindFirstValue(ClaimTypes.NameIdentifier)) is not { } steamId)
            return Results.Redirect($"{back}?steam=failed");
        try
        {
            var problem = await library.LinkAsync(userId, steamId);
            return Results.Redirect($"{back}?steam={(problem is null ? "linked" : "unread")}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("Linking Steam for {UserId} failed: {Message}", userId, ex.Message);
            return Results.Redirect($"{back}?steam=failed");
        }
    }

    // Steam's answer refused (or cancelled): back where they came from, saying so.
    public static Task FailedAsync(RemoteFailureContext context)
    {
        context.Response.Redirect($"{BackOf(context.HttpContext)}?steam=failed");
        context.HandleResponse();
        return Task.CompletedTask;
    }

    private static string BackOf(HttpContext context)
    {
        var back = PanelPath.Local(context.Request.Cookies[BackCookie]) ?? "/";
        context.Response.Cookies.Delete(BackCookie);
        return back;
    }

    // The account in the identity Steam vouches for.
    public static ulong? SteamId(string? claimed) => claimed is not null && ClaimedId().Match(claimed) is { Success: true } id ? ulong.Parse(id.Groups[1].Value) : null;

    [GeneratedRegex(@"^https://steamcommunity\.com/openid/id/(7656119\d{10})$")]
    private static partial Regex ClaimedId();
}
