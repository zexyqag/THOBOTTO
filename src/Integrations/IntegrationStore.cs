using System.Collections.Concurrent;

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

public enum SettingSource
{
    None,
    Stack,
    Panel,
}

// Settings for Last.fm, YouTube and Spotify: what the owner set in the panel, else the deployment's
// configuration (the stack's environment variables). Loaded once at start; changes take effect at once.
public sealed class IntegrationStore(IDbContextFactory<BotDbContext> dbFactory, IDataProtectionProvider protection, IConfiguration config, TimeProvider time)
    : IHostedService
{
    public const string YoutubeRefreshToken = "youtube.refreshToken";
    public const string SpotifyClientId = "spotify.clientId";
    public const string SpotifyClientSecret = "spotify.clientSecret";
    public const string SpotifyAlbumsAndPlaylists = "spotify.albumsAndPlaylists";
    public const string LastfmApiKey = "lastfm.apiKey";
    public const string LastfmApiSecret = "lastfm.apiSecret";
    public const string SpeechEngine = "speech.engine";
    public const string SpeechLocalModel = "speech.localModel";
    public const string SpeechCloudUrl = "speech.cloudUrl";
    public const string SpeechCloudKey = "speech.cloudKey";
    public const string SpeechCloudModel = "speech.cloudModel";

    // Where each can come from in the configuration instead. Spotify's has none: Lavalink reads its own.
    private static readonly Dictionary<string, string> Configured = new()
    {
        [YoutubeRefreshToken] = "Lavalink:YoutubeRefreshToken",
        [LastfmApiKey] = "Lastfm:ApiKey",
        [LastfmApiSecret] = "Lastfm:ApiSecret",
    };

    private readonly IDataProtector _protector = protection.CreateProtector("THOBOTTO.Integrations");
    private readonly ConcurrentDictionary<string, string> _values = new();

    // A key changed in the panel.
    public event Func<string, Task>? Changed;

    public string? Get(string key) => Find(key).Value;

    public (string? Value, SettingSource Source) Find(string key)
        => _values.TryGetValue(key, out var set) ? (set, SettingSource.Panel)
            : Configured.TryGetValue(key, out var path) && config[path] is { Length: > 0 } fromStack ? (fromStack, SettingSource.Stack)
            : (null, SettingSource.None);

    // Null or empty clears the panel's value, so the stack's applies again.
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
            _values[row.Key] = _protector.Unprotect(row.ProtectedValue);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
