using System.Net.Http.Headers;
using System.Text.Json;

using THOBOTTO.Panel;

namespace THOBOTTO.Steam;

// Linking the Steam account a member already connected to their Discord profile, which Discord has verified: their
// connections, read with their leave (the connections scope), for the Discord account logged in to the panel only.
public sealed class SteamFromDiscord(SteamLibrary library, ILogger<SteamFromDiscord> logger) : IDiscordPurpose
{
    public const string Purpose = "steam";

    public string Name => Purpose;

    public string Scopes => "identify connections";

    public async Task<IResult> DoneAsync(HttpContext context, HttpClient http, string accessToken, ulong userId, string back)
    {
        if (PanelAccess.UserId(context.User) != userId)
            return Results.Redirect($"{back}?steam=other");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/users/@me/connections");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var connections = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        if (SteamId(connections.RootElement) is not { } steamId)
        {
            // What Discord said instead: each connection's type and whether it's verified, nothing that names anyone.
            logger.LogInformation("{UserId}'s Discord profile has no verified Steam connection: {Connections}", userId,
                string.Join(", ", connections.RootElement.EnumerateArray().Select(c => $"{(c.TryGetProperty("type", out var type) ? type.GetString() : "?")} verified={(c.TryGetProperty("verified", out var verified) ? verified.ToString() : "missing")}")));
            return Results.Redirect($"{back}?steam=none");
        }
        logger.LogInformation("{UserId} linked Steam from their Discord profile", userId);
        return Results.Redirect($"{back}?steam={(await library.LinkAsync(userId, steamId) is null ? "linked" : "unread")}");
    }

    // The verified Steam account among a member's Discord connections.
    public static ulong? SteamId(JsonElement connections)
        => connections.EnumerateArray()
            .Where(c => c.GetProperty("type").GetString() == "steam" && c.TryGetProperty("verified", out var verified) && verified.GetBoolean())
            .Select(c => ulong.TryParse(c.GetProperty("id").GetString(), out var id) ? id : (ulong?)null)
            .FirstOrDefault(id => id is not null);
}
