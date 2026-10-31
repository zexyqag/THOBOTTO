using System.Collections.Concurrent;
using System.Security.Cryptography;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;

namespace THOBOTTO.Integrations;

// A setting for an outside service, set by the bot's owner in the panel; encrypted with the data protection keys.
public sealed class IntegrationSetting
{
    public required string Key { get; init; }

    public required string ProtectedValue { get; set; }

    public ulong UpdatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// Settings for outside services (Last.fm, YouTube, Spotify, speech), set by the owner in the panel.
// Loaded once at start; changes take effect at once.
public sealed class IntegrationStore(IDbContextFactory<BotDbContext> dbFactory, IDataProtectionProvider protection, IConfiguration config, TimeProvider time, ILogger<IntegrationStore> logger)
    : IHostedService
{
    public const string YoutubeRefreshToken = "youtube.refreshToken";
    public const string SpotifyClientId = "spotify.clientId";
    public const string SpotifyClientSecret = "spotify.clientSecret";
    public const string SpotifyAlbumsAndPlaylists = "spotify.albumsAndPlaylists";
    public const string SpotifyTokenEndpoint = "spotify.tokenEndpoint";
    // none, folder (the image's Archive:Folder) or s3.
    public const string ArchiveStorage = "archive.storage";
    public const string ArchiveS3Url = "archive.s3.serviceUrl";
    public const string ArchiveS3Bucket = "archive.s3.bucket";
    public const string ArchiveS3AccessKey = "archive.s3.accessKey";
    public const string ArchiveS3SecretKey = "archive.s3.secretKey";
    public const string LastfmApiKey = "lastfm.apiKey";
    public const string LastfmApiSecret = "lastfm.apiSecret";
    public const string SpeechEngine = "speech.engine";
    // "on": helpers play through their own voice connection via the relay, so the one playing can also listen.
    public const string VoiceRelay = "voice.relay";
    public const string SpeechLocalModel = "speech.localModel";
    public const string SpeechThreads = "speech.threads";
    // The language model reading voice commands in members' own words (an OpenAI-style server's address).
    public const string UnderstandingUrl = "understanding.url";
    // How helpers speak: off, piper (here), kokoro (on the stack) or cloud; the address, key and model of the last two.
    public const string VoiceEngine = "voice.engine";
    public const string VoiceUrl = "voice.url";
    public const string VoiceKey = "voice.key";
    public const string VoiceModel = "voice.model";
    // For reading linked members' Steam games.
    public const string SteamApiKey = "steam.apiKey";
    // LibreTranslate's address (on the stack, or another instance and its key).
    public const string TranslationUrl = "translation.url";
    public const string TranslationKey = "translation.key";
    // From the bot's OAuth2 page: with it, "Log in with Discord" works on the panel (besides /panel links).
    public const string PanelClientSecret = "panel.clientSecret";
    // Watching together: the token helper's address, a YouTube account's cookies, and when the owner was told they stopped working.
    public const string WatchTokenHelper = "watch.tokenHelper";
    public const string WatchCookies = "watch.cookies";
    public const string WatchCookiesWarned = "watch.cookiesWarned";

    private readonly IDataProtector _protector = protection.CreateProtector("THOBOTTO.Integrations");
    private readonly ConcurrentDictionary<string, string> _values = new();

    // A key changed in the panel.
    public event Func<string, Task>? Changed;

    // When the environment sets one of these, its value wins over the panel's.
    private static readonly Dictionary<string, string> Configured = new()
    {
        [SpotifyTokenEndpoint] = "Spotify:TokenEndpoint",
        [ArchiveStorage] = "Archive:Storage",
        [ArchiveS3Url] = "Archive:S3:ServiceUrl",
        [ArchiveS3Bucket] = "Archive:S3:Bucket",
        [ArchiveS3AccessKey] = "Archive:S3:AccessKey",
        [ArchiveS3SecretKey] = "Archive:S3:SecretKey",
    };

    public string? Get(string key) => FromEnvironment(key) ?? _values.GetValueOrDefault(key);

    public string? SetBy(string key) => FromEnvironment(key) is null ? null : Configured[key].Replace(":", "__");

    private string? FromEnvironment(string key) => Configured.TryGetValue(key, out var path) && config[path] is { Length: > 0 } value ? value : null;

    // Null or empty clears it.
    public async Task SetAsync(string key, string? value, ulong actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.IntegrationSettings.FindAsync(key);
        if (string.IsNullOrEmpty(value))
        {
            if (row is not null)
                db.IntegrationSettings.Remove(row);
            _values.TryRemove(key, out _);
        }
        else
        {
            if (row is null)
                db.IntegrationSettings.Add(row = new() { Key = key, ProtectedValue = "" });
            (row.ProtectedValue, row.UpdatedBy, row.UpdatedAt) = (_protector.Protect(value), actorId, time.GetUtcNow());
            _values[key] = value;
        }
        await db.SaveChangesAsync();
        foreach (var changed in Changed?.GetInvocationList().Cast<Func<string, Task>>() ?? [])
            await changed(key);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        foreach (var row in await db.IntegrationSettings.AsNoTracking().ToListAsync(cancellationToken))
        {
            try
            {
                _values[row.Key] = _protector.Unprotect(row.ProtectedValue);
            }
            catch (CryptographicException)
            {
                logger.LogWarning("The {Key} setting can't be read (data protection keys changed); set it again in the panel", row.Key);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
