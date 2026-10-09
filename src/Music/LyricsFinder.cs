using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace THOBOTTO.Music;

public sealed record LyricLine(long At, string Text);

// Lines carry times when the lyrics are synced; otherwise only the text is there.
public sealed record Lyrics(string Title, string Artist, string Text, IReadOnlyList<LyricLine> Lines);

// Looks lyrics up on LRCLIB (free, no account): by title and artist, else by the cleaned-up title,
// preferring synced lyrics of about the track's length.
public sealed partial class LyricsFinder(ILogger<LyricsFinder> logger)
{
    private const int Remembered = 256;
    private static readonly HttpClient Http = new() { BaseAddress = new("https://lrclib.net/api/"), Timeout = TimeSpan.FromSeconds(10) };
    private readonly ConcurrentDictionary<string, Lyrics?> _found = new();

    static LyricsFinder() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("THOBOTTO (Discord bot)");

    public async Task<Lyrics?> FindAsync(Track track)
    {
        var key = $"{track.Title}\n{track.Author}";
        if (_found.TryGetValue(key, out var known))
            return known;

        Lyrics? lyrics = null;
        try
        {
            // Spotify lists every artist ("A, B"); LRCLIB knows songs by the first.
            var artist = track.Author.Split(',')[0].Trim();
            var title = Clean(track.Title);
            lyrics = Pick(await SearchAsync($"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}"), track)
                ?? Pick(await SearchAsync($"q={Uri.EscapeDataString(title.Contains(artist, StringComparison.OrdinalIgnoreCase) ? title : $"{artist} {title}")}"), track);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogDebug("Lyrics for {Title}: {Message}", track.Title, ex.Message);
            return null;
        }

        if (_found.Count >= Remembered)
            _found.Clear();
        _found[key] = lyrics;
        return lyrics;
    }

    // "Artist - Title (Official Video) [4K]" → "Artist - Title"
    private static string Clean(string title) => Extras().Replace(title, "").Trim();

    private static async Task<List<JsonElement>> SearchAsync(string query)
    {
        using var json = JsonDocument.Parse(await Http.GetStringAsync($"search?{query}"));
        return json.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static Lyrics? Pick(List<JsonElement> hits, Track track)
    {
        var seconds = track.LengthMs / 1000.0;
        var best = hits
            .Where(h => h.GetProperty("plainLyrics").ValueKind == JsonValueKind.String)
            .OrderBy(h => track.IsStream || Math.Abs(h.GetProperty("duration").GetDouble() - seconds) <= 15 ? 0 : 1)
            .ThenBy(h => h.GetProperty("syncedLyrics").ValueKind == JsonValueKind.String ? 0 : 1)
            .ThenBy(h => Math.Abs(h.GetProperty("duration").GetDouble() - seconds))
            .FirstOrDefault();
        if (best.ValueKind == JsonValueKind.Undefined)
            return null;

        var synced = best.GetProperty("syncedLyrics").ValueKind == JsonValueKind.String ? best.GetProperty("syncedLyrics").GetString()! : "";
        var lines = SyncedLine().Matches(synced)
            .Select(m => new LyricLine(int.Parse(m.Groups[1].Value) * 60_000 + (long)(double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) * 1000), m.Groups[3].Value.Trim()))
            .ToList();
        return new(best.GetProperty("trackName").GetString()!, best.GetProperty("artistName").GetString()!, best.GetProperty("plainLyrics").GetString()!, lines);
    }

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]")]
    private static partial Regex Extras();

    [GeneratedRegex(@"^\[(\d+):(\d+(?:\.\d+)?)\](.*)$", RegexOptions.Multiline)]
    private static partial Regex SyncedLine();
}
