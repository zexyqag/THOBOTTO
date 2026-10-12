using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using THOBOTTO.Archive;
using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Helpers;
using THOBOTTO.Integrations;
using THOBOTTO.Music;
using THOBOTTO.Voice.Commands;
using THOBOTTO.Voice.Hearing;
using THOBOTTO.Voice.Relay;
using THOBOTTO.Voice.Speaking;
using THOBOTTO.Watch;

namespace THOBOTTO.Panel.Pages;

public enum CheckState
{
    Good,
    Warning,
    Bad,
    Off,
}

public sealed record Check(string Name, CheckState State, string Detail);

// GitHub's last word on releases: held for an hour, then asked again with its ETag, so an unchanged answer is free.
public sealed record ReleaseAnswer(DateTimeOffset AskedAt, string? Latest, string? ETag, string? Problem)
{
    private static readonly TimeSpan HeldFor = TimeSpan.FromHours(1);

    public bool Holds(DateTimeOffset now) => now - AskedAt < HeldFor;

    // A 304 keeps the latest known, a 404 means none is published, and a refusal keeps what was known for after the hour.
    public static ReleaseAnswer From(ReleaseAnswer? previous, HttpStatusCode status, string? latest, string? etag, DateTimeOffset now)
        => status switch
        {
            HttpStatusCode.NotModified => new(now, previous?.Latest, previous?.ETag, null),
            HttpStatusCode.NotFound => new(now, null, null, null),
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => new(now, previous?.Latest, previous?.ETag, "GitHub asks this address to slow down; asked again later."),
            _ => new(now, latest, etag, null),
        };
}

// The owner's status board: is each outside piece there and working, and are the Lavalink plugins current.
public sealed partial class IntegrationStatus(
    IOptions<LavalinkOptions> lavalink,
    LavalinkSetup setup,
    IntegrationStore store,
    LastfmClient lastfm,
    SpeechToText speech,
    LocalWhisper whisper,
    ParakeetSpeech parakeet,
    Understanding understanding,
    HelperVoices voices,
    HelperFleet fleet,
    DiscordApp app,
    VoiceRelay relay,
    IConfiguration config,
    IWebHostEnvironment environment,
    VideoResolver videos,
    YoutubeCookies cookies,
    IAttachmentStore files,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private ReleaseAnswer? _release;

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
        var checks = await PiecesAsync();
        // Asked last, so GitHub never holds the rest up; shown first.
        checks.Insert(0, await ReleaseAsync());
        return checks;
    }

    private async Task<List<Check>> PiecesAsync()
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
            checks.Add(await ArchiveFilesAsync());
            checks.Add(await DiskAsync());
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
        checks.Add(await SpeechAsync());
        checks.Add(await UnderstandingAsync());
        checks.Add(await VoicesAsync());
        checks.AddRange(OtherChecks());
        checks.Add(await ArchiveFilesAsync());
        checks.Add(await DiskAsync());
        checks.Add(await TokenHelperAsync());
        return checks;
    }

    // This build against the newest release where the bot is published. GitHub is asked at most once an hour, and
    // with the ETag of its last answer, so an unchanged one doesn't count against its limit.
    private async Task<Check> ReleaseAsync()
    {
        if (config["Releases:Repository"] is not { Length: > 0 } repository)
            return new("THOBOTTO", CheckState.Off, "Releases aren't checked: no repository set.");
        var now = time.GetUtcNow();
        var answer = _release;
        if (answer is null || !answer.Holds(now))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
                request.Headers.Add("User-Agent", "THOBOTTO");
                if (answer?.ETag is { } etag)
                    request.Headers.IfNoneMatch.ParseAdd(etag);
                using var response = await Http.SendAsync(request);
                if (response.StatusCode is not (HttpStatusCode.NotModified or HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests))
                    response.EnsureSuccessStatusCode();
                var latest = response.StatusCode == HttpStatusCode.OK ? LatestTag(await response.Content.ReadAsStringAsync()) : null;
                _release = answer = ReleaseAnswer.From(answer, response.StatusCode, latest, response.Headers.ETag?.ToString(), now);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or FormatException)
            {
                return new("THOBOTTO", CheckState.Warning, $"{BotVersion.Running}; couldn't ask about releases: {ex.Message}");
            }
        }
        return answer.Problem is { } problem
            ? new("THOBOTTO", CheckState.Warning, $"{BotVersion.Running}; {problem}")
            : RunningBuild(BotVersion.Running, answer.Latest);
    }

    private static string LatestTag(string json)
    {
        using var document = JsonDocument.Parse(json);
        var tag = document.RootElement.GetProperty("tag_name").GetString()!;
        return tag.StartsWith('v') ? tag[1..] : tag;
    }

    public static Check RunningBuild(string running, string? latest)
        => !BotVersion.IsRelease(running) ? new("THOBOTTO", CheckState.Good, $"{running}: not a release; {(latest is null ? "none is published yet" : $"the latest is {latest}")}.")
            : latest is null ? new("THOBOTTO", CheckState.Good, $"{running}; no release is published yet.")
            : BotVersion.IsNewer(latest, running) ? new("THOBOTTO", CheckState.Warning, $"{running}; {latest} is out.")
            : new("THOBOTTO", CheckState.Good, $"{running}, the latest release.");

    // Where archived attachment files go, and whether it answers.
    private async Task<Check> ArchiveFilesAsync()
    {
        if (!files.Enabled)
            return new("Archive files", CheckState.Off, "Not kept: set Archive:Storage to folder or s3 for the bot.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        return await files.ProblemAsync(timeout.Token) is { } problem
            ? new("Archive files", CheckState.Bad, $"{files.Place}: {problem}")
            : new("Archive files", CheckState.Good, $"{files.Place}.");
    }

    // Watching together: what answers YouTube's checks, and the account for when it insists on signing in.
    // What the bot occupies, and what's left on the drive it writes to.
    private async Task<Check> DiskAsync()
    {
        var videos = config["Watch:Directory"] is { Length: > 0 } directory && Directory.Exists(directory) ? directory : AppContext.BaseDirectory;
        var free = new DriveInfo(Path.GetFullPath(videos)).AvailableFreeSpace / Gigabyte;
        double database;
        await using (var db = await dbFactory.CreateDbContextAsync())
            database = await db.Database.SqlQueryRaw<long>("SELECT pg_database_size(current_database()) AS \"Value\"").SingleAsync() / Gigabyte;
        var taken = $"the database {database:0.##} GB, models {Size(SpeechToText.ModelsFolder(config)):0.##} GB, videos {Size(videos):0.##} GB, temp {Size(Path.GetTempPath()):0.##} GB";
        return new("Disk", free < 1 ? CheckState.Bad : free < 5 ? CheckState.Warning : CheckState.Good,
            $"{free:0.#} GB free where the bot writes; {taken}.{(free < 5 ? " Videos and model downloads may not fit." : "")}");
    }

    private const double Gigabyte = 1024.0 * 1024 * 1024;

    private static double Size(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / Gigabyte : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private async Task<Check> TokenHelperAsync()
    {
        try
        {
            using var ping = JsonDocument.Parse(await GetAsync($"{videos.TokenHelper}/ping"));
            return new("YouTube token helper", CheckState.Good, $"Version {ping.RootElement.GetProperty("version").GetString()} at {videos.TokenHelper}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return new("YouTube token helper", CheckState.Warning, $"Not reachable at {videos.TokenHelper}: videos YouTube checks for bots may not play. Add the youtube-pot service to the stack.");
        }
    }

    // For voice commands: the chosen way of turning speech into text.
    private Task<Check> SpeechAsync()
    {
        if (speech.Engine == SpeechToText.Parakeet)
            return Task.FromResult(new Check("Voice commands", CheckState.Good, $"A free helper listens; Parakeet on this server{(parakeet.Downloaded ? "" : " (downloaded on first use)")}."));
        var model = LocalWhisper.Models.First(m => m.Type == whisper.Model).Name.Split(':')[0];
        return Task.FromResult(new Check("Voice commands", CheckState.Good, $"A free helper listens; Whisper on this server, {model.ToLowerInvariant()} model{(whisper.Downloaded(whisper.Model) ? "" : " (downloaded on first use)")}."));
    }

    private IEnumerable<Check> OtherChecks()
    {
        yield return app.Members ? new("Server Members intent", CheckState.Good, "On: the gate, join sounds and welcomes see who joins.")
            : new("Server Members intent", CheckState.Warning, "Off: the gate and join events don't work. Turn it on in the Developer Portal (Bot → Privileged Gateway Intents), then restart the bot.");
        yield return app.Presences ? new("Presence intent", CheckState.Good, "On: games can name voice channels.")
            : new("Presence intent", CheckState.Warning, "Off: voice channels can't be named after games. Turn it on in the Developer Portal, then restart the bot.");
        var secret = store.Get(IntegrationStore.PanelClientSecret) is not null;
        var built = environment.WebRootFileProvider.GetFileInfo("activity/index.html").Exists;
        yield return !app.Activities ? new("Activity", CheckState.Off, "Activities aren't turned on in the Developer Portal (Activities → Settings); restart the bot after.")
            : !built ? new("Activity", CheckState.Bad, "The Activity's page isn't built into this image.")
            : !secret ? new("Activity", CheckState.Bad, "Needs the client secret (Panel login, above) to sign people in.")
            : new("Activity", CheckState.Good, "On. In the Developer Portal, URL Mappings needs / → this panel's address with /activity.");
        yield return store.Get(IntegrationStore.VoiceRelay) != "on" ? new("Voice relay", CheckState.Off, "Off: helpers play or listen, not both at once.")
            : relay.Running ? new("Voice relay", CheckState.Good, $"Running on {relay.Endpoint}.")
            : new("Voice relay", CheckState.Bad, "Turned on but not running: see the bot's log (a port in use?).");
        foreach (var (name, key, argument) in new[] { ("yt-dlp", "Watch:YtDlp", "--version"), ("ffmpeg", "Watch:Ffmpeg", "-version") })
        {
            var (version, problem) = VoiceCheck.Version(config[key] ?? name, argument);
            yield return problem is null ? new(name, CheckState.Good, $"For watching together: {(version?.Split(' ') is [_, "version", var number, ..] ? number : version)}")
                : new(name, CheckState.Bad, $"Watching together needs it: {problem}");
        }
        var (denoVersion, denoProblem) = VoiceCheck.Version(videos.JavaScript, "--version");
        yield return denoProblem is null ? new("JavaScript for YouTube", CheckState.Good, $"For YouTube's challenges: {denoVersion}")
            : new("JavaScript for YouTube", CheckState.Warning, $"Deno doesn't run ({denoProblem}): videos YouTube challenges may not play.");
        yield return !cookies.Set ? new("YouTube account for videos", CheckState.Off, "None: only needed when YouTube insists on signing in (Watching together, below).")
            : cookies.Working == false ? new("YouTube account for videos", CheckState.Bad, "YouTube no longer takes its cookies: copy them again below.")
            : new("YouTube account for videos", CheckState.Good, "Set; used only when YouTube insists on signing in.");
        var online = fleet.Helpers.Count(h => h.Gateway.Cache.User is not null && h.Lavalink.SessionId is not null);
        yield return new("Helper bots", fleet.Helpers.Count == 0 ? CheckState.Off : online == fleet.Helpers.Count ? CheckState.Good : CheckState.Warning,
            fleet.Helpers.Count == 0 ? "None yet; add them on the Helper bots page." : $"{online} of {fleet.Helpers.Count} connected to Discord and Lavalink.");
    }

    private async Task<Check> VoicesAsync()
        => !voices.On ? new("Helper voices", CheckState.Off, "Off: helpers only write.")
            : await voices.VoicesAsync() is { Count: > 0 } listed ? new("Helper voices", CheckState.Good, $"{voices.Engine}: {listed.Count} voices.")
            : new("Helper voices", CheckState.Bad, $"{voices.Engine} doesn't answer or lists no voices.");

    private async Task<Check> UnderstandingAsync()
        => !understanding.On ? new("Own words", CheckState.Off, "No language model: voice commands take the set phrases only.")
            : await understanding.ReachableAsync() ? new("Own words", CheckState.Good, "The language model answers.")
            : new("Own words", CheckState.Bad, "The language model server doesn't answer at that address.");

    private static async Task<Check> PluginAsync(string name, string running)
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
        return store.Get(IntegrationStore.YoutubeRefreshToken) is null ? new("YouTube sign-in", CheckState.Off, "Not signed in. Optional: it helps when YouTube asks to sign in.")
            : setup.YoutubeSignedIn switch
            {
                true => new("YouTube sign-in", CheckState.Good, "Signed in."),
                false => new("YouTube sign-in", CheckState.Warning, "Google refused the token; YouTube plays signed out. Sign in again below."),
                null => new("YouTube sign-in", CheckState.Warning, "Lavalink hasn't been given the sign-in yet."),
            };
    }

    private async Task<Check> SpotifyAsync()
    {
        var options = lavalink.Value;
        try
        {
            var track = await LavalinkClient.LoadAsync(options, SpotifyTrack, 0);
            if (track.Error is not null || track.Tracks.Count == 0)
                return new("Spotify", CheckState.Bad, $"Songs don't load ({track.Error ?? "nothing found"}). Are the client ID and secret right?");
            var album = await LavalinkClient.LoadAsync(options, SpotifyAlbum, 0);
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
