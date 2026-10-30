using System.Text.RegularExpressions;

using Microsoft.AspNetCore.WebUtilities;

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

    public static string VideoAlone(string identifier)
    {
        if (!Uri.TryCreate(identifier, UriKind.Absolute, out var uri) || !YoutubeHost().IsMatch(uri.Host))
            return identifier;
        var query = QueryHelpers.ParseQuery(uri.Query);
        if (!query.ContainsKey("list") || !query.ContainsKey("v") && uri.Host != "youtu.be")
            return identifier;
        return QueryHelpers.AddQueryString(uri.GetLeftPart(UriPartial.Path), query.Where(q => q.Key is "v" or "t").Select(q => KeyValuePair.Create(q.Key, (string?)q.Value.ToString())));
    }

    [GeneratedRegex(@"^((www|m|music)\.)?youtube\.com$|^youtu\.be$", RegexOptions.IgnoreCase)]
    private static partial Regex YoutubeHost();

    // A host with a path and no scheme: youtu.be/abc, www.youtube.com/watch?v=abc.
    [GeneratedRegex(@"^(?:[a-z0-9-]+\.)+[a-z]{2,}(?::\d+)?/\S*$", RegexOptions.IgnoreCase)]
    private static partial Regex BareLink();

    [GeneratedRegex(@"^[a-z]{2,5}search:")]
    private static partial Regex SourcePrefix();
}

public static class ListLinks
{
    public const string Video = "video";
    public const string List = "list";
}
