using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using THOBOTTO.Integrations;

namespace THOBOTTO.Lastfm;

public sealed record Scrobble(string Artist, string Title, int? Seconds, DateTimeOffset StartedAt);

// A song as Last.fm names it; the same song whatever the case.
public sealed record Song(string Artist, string Title)
{
    public string Key => $"{Artist}\n{Title}".ToLowerInvariant();
}

public sealed class LastfmException(int code, string message) : Exception(message)
{
    // 9: the session key is no longer valid (the member revoked access).
    public bool SessionInvalid => code == 9;
}

// Last.fm's API: signed calls (parameters sorted, name+value concatenated, the secret appended, MD5).
public sealed class LastfmClient(IntegrationStore settings)
{
    private static readonly HttpClient Http = new() { BaseAddress = new("https://ws.audioscrobbler.com/2.0/"), Timeout = TimeSpan.FromSeconds(10) };

    static LastfmClient() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("THOBOTTO (Discord bot)");

    private string? ApiKey => settings.Get(IntegrationStore.LastfmApiKey);

    private string? ApiSecret => settings.Get(IntegrationStore.LastfmApiSecret);

    // Scrobbling and blends need the bot's own Last.fm API account.
    public bool Configured => !string.IsNullOrEmpty(ApiKey) && !string.IsNullOrEmpty(ApiSecret);

    // Where the member approves the bot; Last.fm sends them back to the callback with a token.
    public string AuthorizeUrl(string callback) => $"https://www.last.fm/api/auth/?api_key={ApiKey}&cb={Uri.EscapeDataString(callback)}";

    public async Task<(string Username, string SessionKey)> GetSessionAsync(string token)
    {
        var session = (await CallAsync(HttpMethod.Get, new() { ["method"] = "auth.getSession", ["token"] = token })).GetProperty("session");
        return (session.GetProperty("name").GetString()!, session.GetProperty("key").GetString()!);
    }

    public Task NowPlayingAsync(string sessionKey, Scrobble track)
    {
        var call = new Dictionary<string, string> { ["method"] = "track.updateNowPlaying", ["sk"] = sessionKey, ["artist"] = track.Artist, ["track"] = track.Title };
        if (track.Seconds is { } seconds)
            call["duration"] = seconds.ToString();
        return CallAsync(HttpMethod.Post, call);
    }

    public Task ScrobbleAsync(string sessionKey, Scrobble track)
    {
        var call = new Dictionary<string, string>
        {
            ["method"] = "track.scrobble",
            ["sk"] = sessionKey,
            ["artist[0]"] = track.Artist,
            ["track[0]"] = track.Title,
            ["timestamp[0]"] = track.StartedAt.ToUnixTimeSeconds().ToString(),
        };
        if (track.Seconds is { } seconds)
            call["duration[0]"] = seconds.ToString();
        return CallAsync(HttpMethod.Post, call);
    }

    // Public listening data, no session needed.
    public async Task<IReadOnlyList<Song>> TopTracksAsync(string username, int limit = 50)
        => Songs(await CallAsync(HttpMethod.Get, new() { ["method"] = "user.getTopTracks", ["user"] = username, ["period"] = "3month", ["limit"] = limit.ToString() }, signed: false), "toptracks");

    public async Task<IReadOnlyList<Song>> LovedTracksAsync(string username, int limit = 50)
        => Songs(await CallAsync(HttpMethod.Get, new() { ["method"] = "user.getLovedTracks", ["user"] = username, ["limit"] = limit.ToString() }, signed: false), "lovedtracks");

    public async Task<IReadOnlyList<Song>> SimilarAsync(Song song, int limit = 10)
        => Songs(await CallAsync(HttpMethod.Get, new() { ["method"] = "track.getSimilar", ["artist"] = song.Artist, ["track"] = song.Title, ["autocorrect"] = "1", ["limit"] = limit.ToString() }, signed: false), "similartracks");

    // An artist's tags, most used first, with how much (0–100 relative to the top one).
    public async Task<IReadOnlyList<(string Tag, int Weight)>> ArtistTagsAsync(string artist)
    {
        var root = await CallAsync(HttpMethod.Get, new() { ["method"] = "artist.getTopTags", ["artist"] = artist, ["autocorrect"] = "1" }, signed: false);
        if (!root.TryGetProperty("toptags", out var top) || !top.TryGetProperty("tag", out var tags))
            return [];
        var items = tags.ValueKind == JsonValueKind.Array ? tags.EnumerateArray().ToList() : [tags];
        return items.Select(t => (t.GetProperty("name").GetString() ?? "", t.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0)).ToList();
    }

    // Last.fm gives an object instead of a one-item list, and the artist as {"name"} or {"#text"}.
    public static List<Song> Songs(JsonElement root, string list)
    {
        if (!root.TryGetProperty(list, out var container) || !container.TryGetProperty("track", out var tracks))
            return [];
        var items = tracks.ValueKind == JsonValueKind.Array ? tracks.EnumerateArray().ToList() : [tracks];
        return items
            .Select(t => (Title: t.GetProperty("name").GetString(), Artist: t.GetProperty("artist") is var a && a.ValueKind == JsonValueKind.Object
                ? (a.TryGetProperty("name", out var n) ? n.GetString() : a.TryGetProperty("#text", out var x) ? x.GetString() : null)
                : a.GetString()))
            .Where(t => !string.IsNullOrWhiteSpace(t.Title) && !string.IsNullOrWhiteSpace(t.Artist))
            .Select(t => new Song(t.Artist!, t.Title!))
            .ToList();
    }

    private async Task<JsonElement> CallAsync(HttpMethod method, Dictionary<string, string> parameters, bool signed = true)
    {
        parameters["api_key"] = ApiKey!;
        if (signed)
            parameters["api_sig"] = Sign(parameters, ApiSecret!);
        parameters["format"] = "json";

        using var request = method == HttpMethod.Get
            ? new HttpRequestMessage(method, "?" + string.Join('&', parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}")))
            : new HttpRequestMessage(method, "") { Content = new FormUrlEncodedContent(parameters) };
        using var response = await Http.SendAsync(request);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement.Clone();
        if (root.TryGetProperty("error", out var error))
            throw new LastfmException(error.GetInt32(), root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "");
        return root;
    }

    public static string Sign(IReadOnlyDictionary<string, string> parameters, string secret)
    {
        var text = new StringBuilder();
        foreach (var (key, value) in parameters.Where(p => p.Key is not ("format" or "callback")).OrderBy(p => p.Key, StringComparer.Ordinal))
            text.Append(key).Append(value);
        text.Append(secret);
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
