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
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<bool> AvailableAsync(IntegrationStore store, PanelLinks links)
        => store.Get(IntegrationStore.PanelClientSecret) is not null && await links.AddressAsync() is not null;

    public static async Task<IResult> StartAsync(HttpContext context, IConfiguration config, IntegrationStore store, PanelLinks links)
    {
        if (store.Get(IntegrationStore.PanelClientSecret) is null || await links.AddressAsync() is not { } address)
            return Results.Redirect("/");
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
        context.Response.Cookies.Append(StateCookie, state, new() { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(10) });
        return Results.Redirect($"https://discord.com/oauth2/authorize?client_id={ClientId(config)}&response_type=code&scope=identify"
            + $"&redirect_uri={Uri.EscapeDataString(address + PanelLinks.Callback)}&state={state}");
    }

    public static async Task<IResult> CallbackAsync(HttpContext context, string? code, string? state, IConfiguration config, IntegrationStore store, PanelLinks links, ILogger logger)
    {
        var expected = context.Request.Cookies[StateCookie];
        context.Response.Cookies.Delete(StateCookie);
        if (code is null || state is null || state != expected
            || store.Get(IntegrationStore.PanelClientSecret) is not { } secret || await links.AddressAsync() is not { } address)
            return Results.Redirect("/");
        try
        {
            using var exchange = await Http.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
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
            using var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var user = await response.Content.ReadFromJsonAsync<JsonElement>();
            var name = user.TryGetProperty("global_name", out var global) && global.GetString() is { } shown ? shown : user.GetProperty("username").GetString()!;
            await SignInAsync(context, ulong.Parse(user.GetProperty("id").GetString()!), name);
            return Results.Redirect("/");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException)
        {
            logger.LogWarning("Logging in with Discord failed: {Message}", ex.Message);
            return Results.Redirect("/");
        }
    }

    public static async Task<IResult> LinkAsync(HttpContext context, string? token, PanelLinks links)
    {
        if (token is null || links.Redeem(token) is not { } login)
            return Results.Text("That login link has expired or was used already. Type /panel in Discord for a new one.");
        await SignInAsync(context, login.UserId, login.Name);
        return Results.Redirect(login.GuildId is { } guildId ? $"/g/{guildId}" : "/");
    }

    public static Task SignInAsync(HttpContext context, ulong userId, string name)
        => context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, userId.ToString()), new(ClaimTypes.Name, name)], CookieAuthenticationDefaults.AuthenticationScheme)));

    // The bot's application id is its user id, which its token starts with.
    private static ulong ClientId(IConfiguration config) => new BotToken(config["Discord:Token"]!).Id;
}
