using System.Text.RegularExpressions;

using THOBOTTO.Music;

namespace THOBOTTO.Lastfm;

// Artist and title as Last.fm knows them. Spotify and YouTube Music give them cleanly; a YouTube video
// is usually "Artist - Title (Official Video)" on someone's channel.
public static partial class TrackNames
{
    // Null for what isn't scrobbled: streams, and tracks under 30 seconds (Last.fm's rule).
    public static Scrobble? From(Track track, DateTimeOffset startedAt)
    {
        if (track.IsStream || track.LengthMs < 30_000)
            return null;

        var title = Noise().Replace(track.Title, "").Trim();
        // Spotify lists every artist ("A, B"); Last.fm files the song under the first.
        var artist = track.Source == "spotify" ? track.Author.Split(',')[0].Trim() : ChannelSuffix().Replace(track.Author, "").Trim();
        if (track.Source != "spotify" && !track.Author.EndsWith(" - Topic") && title.Split(" - ", 2) is [var left, var right])
            (artist, title) = (left.Trim(), right.Trim());
        return artist.Length > 0 && title.Length > 0 ? new(artist, title, (int)(track.LengthMs / 1000), startedAt) : null;
    }

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*\b(official|video|audio|lyrics?|visuali[sz]er|hd|hq|4k|remaster(ed)?|mv)\b[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"( - Topic|VEVO|Official)$", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelSuffix();
}
