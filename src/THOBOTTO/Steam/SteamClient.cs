using System.Text.Json;

using THOBOTTO.Integrations;

namespace THOBOTTO.Steam;

public sealed record SteamPlayer(string Name);

public sealed record OwnedGame(int AppId, string Name, int Minutes);

// Steam's Web API (with the key from the Integrations page) and its store.
public sealed class SteamClient(IntegrationStore store, [FromKeyedServices(nameof(SteamClient))] HttpClient http)
{
    // Categories on a store page that mean it's played with others: multi-player, co-op, MMO, cross-platform,
    // online PvP and co-op, Remote Play Together, LAN, PvP.
    private static readonly HashSet<int> TogetherCategories = [1, 9, 20, 27, 36, 38, 44, 47, 48, 49];

    public bool Configured => Key is not null;

    private string? Key => store.Get(IntegrationStore.SteamApiKey);

    public async Task<SteamPlayer?> PlayerAsync(ulong steamId, CancellationToken ct = default)
    {
        using var json = await GetAsync($"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={Key}&steamids={steamId}", ct);
        return json.RootElement.GetProperty("response").GetProperty("players").EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } player
            ? new(player.GetProperty("personaname").GetString() ?? steamId.ToString())
            : null;
    }

    // Null when their game details aren't public.
    public async Task<IReadOnlyList<OwnedGame>?> GamesAsync(ulong steamId, CancellationToken ct = default)
    {
        using var json = await GetAsync($"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={Key}&steamid={steamId}&include_appinfo=1&include_played_free_games=1", ct);
        return Owned(json.RootElement);
    }

    public static IReadOnlyList<OwnedGame>? Owned(JsonElement answer)
    {
        var response = answer.GetProperty("response");
        if (!response.TryGetProperty("game_count", out _))
            return null;
        return response.TryGetProperty("games", out var games)
            ? [.. games.EnumerateArray().Select(g => new OwnedGame(g.GetProperty("appid").GetInt32(), g.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "", g.TryGetProperty("playtime_forever", out var minutes) ? minutes.GetInt32() : 0))
                .Where(g => g.Name.Length > 0)]
            : [];
    }

    // Null when the store has no page for it.
    public async Task<bool?> TogetherAsync(int appId, CancellationToken ct = default)
    {
        using var json = await GetAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&filters=categories", ct);
        return Together(json.RootElement, appId);
    }

    public static bool? Together(JsonElement answer, int appId)
    {
        if (!answer.TryGetProperty(appId.ToString(), out var app) || !app.TryGetProperty("success", out var success) || !success.GetBoolean())
            return null;
        return app.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("categories", out var categories)
            && categories.EnumerateArray().Any(c => TogetherCategories.Contains(c.GetProperty("id").GetInt32()));
    }

    // Null when the key works, else why not.
    public async Task<string?> KeyProblemAsync()
    {
        try
        {
            await PlayerAsync(76561197960435530);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return ex.Message;
        }
    }

    private async Task<JsonDocument> GetAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }
}
