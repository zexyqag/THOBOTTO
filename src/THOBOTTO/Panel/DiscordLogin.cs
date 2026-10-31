using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

using NetCord;

using THOBOTTO.Integrations;

namespace THOBOTTO.Panel;

// Something a feature asks Discord for besides who someone is (a scope beyond identify), through the panel's own
// /signin-discord redirect: instead of logging in, Done gets the member's access token.
public interface IDiscordPurpose
{
    string Name { get; }

    string Scopes { get; }

    Task<IResult> DoneAsync(HttpContext context, HttpClient http, string accessToken, ulong userId, string back);
}

// Signing in to the panel: "Log in with Discord" (OAuth, when the owner set the client secret on the
// Integrations page), a /panel link, or (in development) as anyone. The same OAuth serves a feature's purpose.
public static class DiscordLogin
{
    private const string StateCookie = "thobotto.login";
    // Where to go after logging in (a page of the panel's own), when not the start.
    private const string BackCookie = "thobotto.back";
    private const string PurposeCookie = "thobotto.purpose";

    public static async Task<bool> AvailableAsync(IntegrationStore store, PanelLinks links)
        => store.Get(IntegrationStore.PanelClientSecret) is not null && await links.AddressAsync() is not null;

    public static async Task<IResult> StartAsync(HttpContext context, IConfiguration config, IntegrationStore store, PanelLinks links, string? back = null, IDiscordPurpose? purpose = null)
    {
        if (store.Get(IntegrationStore.PanelClientSecret) is null || await links.AddressAsync() is not { } address)
            return Results.Redirect("/");
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
        var cookie = new CookieOptions { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(10) };
        context.Response.Cookies.Append(StateCookie, state, cookie);
        if (PanelPath.Local(back) is { } local)
            context.Response.Cookies.Append(BackCookie, local, cookie);
        if (purpose is not null)
            context.Response.Cookies.Append(PurposeCookie, purpose.Name, cookie);
        else
            context.Response.Cookies.Delete(PurposeCookie);
        return Results.Redirect($"https://discord.com/oauth2/authorize?client_id={ClientId(config)}&response_type=code&scope={Uri.EscapeDataString(purpose?.Scopes ?? "identify")}"
            + $"&redirect_uri={Uri.EscapeDataString(address + PanelLinks.Callback)}&state={state}");
    }

    public static async Task<IResult> CallbackAsync(HttpContext context, string? code, string? state, IConfiguration config, IntegrationStore store, PanelLinks links,
        IHttpClientFactory clients, IEnumerable<IDiscordPurpose> purposes, ILogger logger)
    {
        var expected = context.Request.Cookies[StateCookie];
        var back = context.Request.Cookies[BackCookie];
        var purpose = purposes.FirstOrDefault(p => p.Name == context.Request.Cookies[PurposeCookie]);
        context.Response.Cookies.Delete(StateCookie);
        context.Response.Cookies.Delete(BackCookie);
        context.Response.Cookies.Delete(PurposeCookie);
        if (code is null || state is null || state != expected
            || store.Get(IntegrationStore.PanelClientSecret) is not { } secret || await links.AddressAsync() is not { } address)
            return Results.Redirect("/");
        try
        {
            var http = clients.CreateClient("panel");
            using var exchange = await http.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId(config).ToString(),
                ["client_secret"] = secret,
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = address + PanelLinks.Callback,
            }));
            exchange.EnsureSuccessStatusCode();
            var accessToken = (await exchange.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString();
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/users/@me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var user = await response.Content.ReadFromJsonAsync<JsonElement>();
            var id = ulong.Parse(user.GetProperty("id").GetString()!);
            if (purpose is not null)
                return await purpose.DoneAsync(context, http, accessToken!, id, PanelPath.Local(back) ?? "/");
            var name = user.TryGetProperty("global_name", out var global) && global.GetString() is { } shown ? shown : user.GetProperty("username").GetString()!;
            await SignInAsync(context, id, name);
            return Results.Redirect(PanelPath.Local(back) ?? "/");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException)
        {
            logger.LogWarning("Logging in with Discord failed: {Message}", ex.Message);
            return Results.Redirect("/");
        }
    }

    // back: where to go after (a sign-in elsewhere that needs the member logged in first).
    public static async Task<IResult> LinkAsync(HttpContext context, string? token, string? back, PanelLinks links)
    {
        // Discord looking at the link for a preview mustn't use it up.
        if (context.Request.Headers.UserAgent.ToString().Contains("Discordbot"))
            return Results.Text("THOBOTTO panel login");
        if (token is null || links.Redeem(token) is not { } login)
            return Results.Text("That login link has expired (they last 10 minutes) or was used already. Type /panel in Discord for a new one.");
        await SignInAsync(context, login.UserId, login.Name);
        return Results.Redirect(PanelPath.Local(back) ?? (login.GuildId is { } guildId ? $"/g/{guildId}" : "/"));
    }

    public static Task SignInAsync(HttpContext context, ulong userId, string name)
        => context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, userId.ToString()), new(ClaimTypes.Name, name)], CookieAuthenticationDefaults.AuthenticationScheme)));

    // The bot's application id is its user id, which its token starts with.
    private static ulong ClientId(IConfiguration config) => new BotToken(config["Discord:Token"]!).Id;
}
