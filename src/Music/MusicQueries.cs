using System.Text.RegularExpressions;

using NetCord.Services.ApplicationCommands;

namespace THOBOTTO.Music;

// Where plain search words go when asked for, as a command option.
public enum SearchOn
{
    YouTube,
    [SlashCommandChoice(Name = "YouTube Music")] YouTubeMusic,
    SoundCloud,
    Spotify,
}

// What someone typed to play, as Lavalink gets it: a link as typed (one without https:// counts, youtu.be/… and
// the like), an explicit source ("scsearch:…") as typed, plain words to the search picked or the server's default.
public static partial class MusicQueries
{
    public static string Prefix(SearchOn on) => on switch
    {
        SearchOn.YouTubeMusic => "ytmsearch",
        SearchOn.SoundCloud => "scsearch",
        SearchOn.Spotify => "spsearch",
        _ => "ytsearch",
    };

    public static bool AsTyped(string text) => Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" || SourcePrefix().IsMatch(text);

    public static string Identifier(string text, string search)
    {
        text = text.Trim();
        if (AsTyped(text))
            return text;
        if (BareLink().IsMatch(text))
            return "https://" + text;
        return $"{search}:{text}";
    }

    // A host with a path and no scheme: youtu.be/abc, www.youtube.com/watch?v=abc.
    [GeneratedRegex(@"^(?:[a-z0-9-]+\.)+[a-z]{2,}(?::\d+)?/\S*$", RegexOptions.IgnoreCase)]
    private static partial Regex BareLink();

    [GeneratedRegex(@"^[a-z]{2,5}search:")]
    private static partial Regex SourcePrefix();
}
