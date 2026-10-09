using System.Text.Json;

namespace THOBOTTO.Music;

public sealed record Track(string Encoded, string Title, string Author, string? Uri, long LengthMs, bool IsStream, string Source, ulong RequestedBy, string? Identifier = null)
{
    public static Track From(JsonElement track, ulong requestedBy)
    {
        var info = track.GetProperty("info");
        return new(
            track.GetProperty("encoded").GetString()!,
            info.GetProperty("title").GetString() ?? "Unknown",
            info.GetProperty("author").GetString() ?? "",
            info.TryGetProperty("uri", out var uri) ? uri.GetString() : null,
            info.GetProperty("length").GetInt64(),
            info.GetProperty("isStream").GetBoolean(),
            info.GetProperty("sourceName").GetString() ?? "",
            requestedBy,
            info.GetProperty("identifier").GetString());
    }

    public string Length => IsStream ? "live" : Duration(LengthMs);

    public string Markdown => Uri is null ? $"**{Title}**" : $"[**{Title}**]({Uri})";

    public static string Duration(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}

public sealed record LoadResult(IReadOnlyList<Track> Tracks, string? Playlist, string? Error);
