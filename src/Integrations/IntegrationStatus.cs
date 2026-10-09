using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Options;

using THOBOTTO.Helpers;
using THOBOTTO.Lastfm;
using THOBOTTO.Music;

namespace THOBOTTO.Integrations;

public enum CheckState
{
    Good,
    Warning,
    Bad,
    Off,
}

public sealed record Check(string Name, CheckState State, string Detail);

// The owner's status board: is each outside piece there and working, and are the Lavalink plugins current.
public sealed partial class IntegrationStatus(
    IOptions<LavalinkOptions> lavalink,
    LavalinkSetup setup,
    IntegrationStore store,
    LastfmClient lastfm,
    HelperFleet fleet)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // Where each plugin is published: releases, and builds of every commit.
    private static readonly Dictionary<string, string> Published = new()
    {
        ["youtube-plugin"] = "dev/lavalink/youtube/youtube-plugin",
        ["lavasrc-plugin"] = "com/github/topi314/lavasrc/lavasrc-plugin",
    };

    // Songs every check can rely on existing.
    private const string YoutubeVideo = "dQw4w9WgXcQ";
    private const string SpotifyTrack = "https://open.spotify.com/track/4PTG3Z6ehGkBFwjybzWkR8";
    private const string SpotifyAlbum = "https://open.spotify.com/album/7qemUq4n71awwVPOaX7jw4";

    public async Task<IReadOnlyList<Check>> CheckAsync()
    {
        var checks = new List<Check>();
        var options = lavalink.Value;
        JsonElement info;
        try
        {
            using var json = JsonDocument.Parse(await GetAsync($"{options.BaseAddress}/v4/info"));
            info = json.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            checks.Add(new("Lavalink", CheckState.Bad, $"Not reachable at {options.BaseAddress}: {ex.Message}"));
            checks.AddRange(OtherChecks());
            checks.Add(await LastfmAsync());
            return checks;
        }

        checks.Add(new("Lavalink", CheckState.Good, $"Version {info.GetProperty("version").GetProperty("semver").GetString()}"));
        foreach (var plugin in info.GetProperty("plugins").EnumerateArray())
            checks.Add(await PluginAsync(plugin.GetProperty("name").GetString()!, plugin.GetProperty("version").GetString()!));
        var sources = info.GetProperty("sourceManagers").EnumerateArray().Select(s => s.GetString()).ToHashSet();

        if (sources.Contains("youtube"))
        {
            checks.Add(await YoutubeAsync());
            checks.Add(YoutubeSignIn());
        }
        else
            checks.Add(new("YouTube", CheckState.Off, "The YouTube plugin isn't enabled in Lavalink."));
        checks.Add(sources.Contains("spotify") ? await SpotifyAsync() : new("Spotify", CheckState.Off, "LavaSrc's Spotify source isn't enabled in Lavalink."));
        checks.Add(await LastfmAsync());
        checks.AddRange(OtherChecks());
        return checks;
    }

    private IEnumerable<Check> OtherChecks()
    {
        var online = fleet.Helpers.Count(h => h.Gateway.Cache.User is not null && h.Lavalink.SessionId is not null);
        yield return new("Helper bots", fleet.Helpers.Count == 0 ? CheckState.Off : online == fleet.Helpers.Count ? CheckState.Good : CheckState.Warning,
            fleet.Helpers.Count == 0 ? "None yet; add them on the Helper bots page." : $"{online} of {fleet.Helpers.Count} connected to Discord and Lavalink.");
    }

    private async Task<Check> PluginAsync(string name, string running)
    {
        if (!Published.TryGetValue(name, out var path))
            return new(name, CheckState.Good, $"Version {Short(running)}");
        try
        {
            var release = Release().Match(await Http.GetStringAsync($"https://maven.lavalink.dev/releases/{path}/maven-metadata.xml")).Groups[1].Value;
            var builds = Version().Matches(await Http.GetStringAsync($"https://maven.lavalink.dev/snapshots/{path}/maven-metadata.xml")).Select(m => m.Groups[1].Value).ToList();
            var latestBuild = builds.LastOrDefault();
            // A commit build is current while no newer one is out; a release while it's the latest release.
            var commit = running.Length == 40;
            var current = commit ? running == latestBuild : running == release;
            var detail = $"Running {Short(running)}{(commit ? " (a build between releases)" : "")}; latest release {release}" + (commit && latestBuild is not null ? $", latest build {Short(latestBuild)}" : "");
            return new(name, current ? CheckState.Good : CheckState.Warning, current ? detail : $"{detail}. A newer one is out: the version is in the stack's LAVALINK_PLUGINS settings.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(name, CheckState.Warning, $"Running {Short(running)}; couldn't check for newer ones ({ex.Message}).");
        }
    }

    private async Task<Check> YoutubeAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{lavalink.Value.BaseAddress}/youtube/stream/{YoutubeVideo}");
            request.Headers.Add("Authorization", lavalink.Value.Passphrase);
            request.Headers.Range = new(0, 64_000);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            return response.IsSuccessStatusCode
                ? new("YouTube", CheckState.Good, "Plays.")
                : new("YouTube", CheckState.Bad, $"A test video doesn't play ({(int)response.StatusCode}). Signing in, or a newer YouTube plugin, usually helps.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new("YouTube", CheckState.Bad, $"Checking failed: {ex.Message}");
        }
    }

    private Check YoutubeSignIn()
    {
        var (token, source) = store.Find(IntegrationStore.YoutubeRefreshToken);
        var from = source == SettingSource.Panel ? "set here" : "from the stack";
        return token is null ? new("YouTube sign-in", CheckState.Off, "Not signed in. Optional: it helps when YouTube asks to sign in.")
            : setup.YoutubeSignedIn switch
            {
                true => new("YouTube sign-in", CheckState.Good, $"Signed in (token {from})."),
                false => new("YouTube sign-in", CheckState.Warning, $"Google refused the token ({from}); YouTube plays signed out. Sign in again below."),
                null => new("YouTube sign-in", CheckState.Warning, $"A token is {from}, but Lavalink hasn't been given it yet."),
            };
    }

    private async Task<Check> SpotifyAsync()
    {
        var options = lavalink.Value;
        try
        {
            var track = await LavalinkConnection.LoadAsync(options, SpotifyTrack, 0);
            if (track.Error is not null || track.Tracks.Count == 0)
                return new("Spotify", CheckState.Bad, $"Songs don't load ({track.Error ?? "nothing found"}). Are the client ID and secret right?");
            var album = await LavalinkConnection.LoadAsync(options, SpotifyAlbum, 0);
            return album.Error is null && album.Tracks.Count > 0
                ? new("Spotify", CheckState.Good, "Songs, albums and playlists load.")
                : new("Spotify", CheckState.Warning, "Single songs load; albums and playlists don't (they need the Spotify token service and albums & playlists mode).");
        }
        catch (HttpRequestException ex)
        {
            return new("Spotify", CheckState.Bad, $"Checking failed: {ex.Message}");
        }
    }

    private async Task<Check> LastfmAsync()
    {
        if (!lastfm.Configured)
            return new("Last.fm", CheckState.Off, "No API key: scrobbling, blends and genres are off.");
        try
        {
            await lastfm.ArtistTagsAsync("Rick Astley");
            return new("Last.fm", CheckState.Good, "The API key works.");
        }
        catch (Exception ex) when (ex is LastfmException or HttpRequestException or TaskCanceledException or JsonException)
        {
            return new("Last.fm", CheckState.Bad, $"Last.fm refused the API key or isn't reachable: {ex.Message}");
        }
    }

    private async Task<string> GetAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", lavalink.Value.Passphrase);
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static string Short(string version) => version.Length == 40 ? version[..7] : version;

    [GeneratedRegex("<release>([^<]+)</release>")]
    private static partial Regex Release();

    [GeneratedRegex("<version>([^<]+)</version>")]
    private static partial Regex Version();
}
