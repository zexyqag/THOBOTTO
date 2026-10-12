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

// Signing in to the panel: "Log in with Discord" (OAuth, when the owner set the client secret on the
// Integrations page), a /panel link, or (in development) as anyone.
public static class DiscordLogin
{
    private const string StateCookie = "thobotto.login";
    // Where to go after logging in (a page of the panel's own), when not the start.
    private const string BackCookie = "thobotto.back";

    public static async Task<bool> AvailableAsync(IntegrationStore store, PanelLinks links)
        => store.Get(IntegrationStore.PanelClientSecret) is not null && await links.AddressAsync() is not null;

    public static async Task<IResult> StartAsync(HttpContext context, IConfiguration config, IntegrationStore store, PanelLinks links, string? back = null)
    {
        if (store.Get(IntegrationStore.PanelClientSecret) is null || await links.AddressAsync() is not { } address)
            return Results.Redirect("/");
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
        var cookie = new CookieOptions { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(10) };
        context.Response.Cookies.Append(StateCookie, state, cookie);
        if (Local(back))
            context.Response.Cookies.Append(BackCookie, back!, cookie);
        return Results.Redirect($"https://discord.com/oauth2/authorize?client_id={ClientId(config)}&response_type=code&scope=identify"
            + $"&redirect_uri={Uri.EscapeDataString(address + PanelLinks.Callback)}&state={state}");
    }

    public static async Task<IResult> CallbackAsync(HttpContext context, string? code, string? state, IConfiguration config, IntegrationStore store, PanelLinks links, IHttpClientFactory clients, ILogger logger)
    {
        var expected = context.Request.Cookies[StateCookie];
        var back = context.Request.Cookies[BackCookie];
        context.Response.Cookies.Delete(StateCookie);
        context.Response.Cookies.Delete(BackCookie);
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
            var name = user.TryGetProperty("global_name", out var global) && global.GetString() is { } shown ? shown : user.GetProperty("username").GetString()!;
            await SignInAsync(context, ulong.Parse(user.GetProperty("id").GetString()!), name);
            return Results.Redirect(Local(back) ? back! : "/");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException)
        {
            logger.LogWarning("Logging in with Discord failed: {Message}", ex.Message);
            return Results.Redirect("/");
        }
    }

    public static async Task<IResult> LinkAsync(HttpContext context, string? token, PanelLinks links)
    {
        // Discord looking at the link for a preview mustn't use it up.
        if (context.Request.Headers.UserAgent.ToString().Contains("Discordbot"))
            return Results.Text("THOBOTTO panel login");
        if (token is null || links.Redeem(token) is not { } login)
            return Results.Text("That login link has expired (they last 10 minutes) or was used already. Type /panel in Discord for a new one.");
        await SignInAsync(context, login.UserId, login.Name);
        return Results.Redirect(login.GuildId is { } guildId ? $"/g/{guildId}" : "/");
    }

    public static Task SignInAsync(HttpContext context, ulong userId, string name)
        => context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, userId.ToString()), new(ClaimTypes.Name, name)], CookieAuthenticationDefaults.AuthenticationScheme)));

    // Only a path of the panel's own: never off to another site.
    private static bool Local(string? path) => path is ['/', not '/' and not '\\', ..];

    // The bot's application id is its user id, which its token starts with.
    private static ulong ClientId(IConfiguration config) => new BotToken(config["Discord:Token"]!).Id;
}
