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

// Settings for outside services (Last.fm, YouTube, Spotify, speech), set by the owner in the panel.
// Loaded once at start; changes take effect at once.
public sealed class IntegrationStore(IDbContextFactory<BotDbContext> dbFactory, IDataProtectionProvider protection, TimeProvider time)
    : IHostedService
{
    public const string YoutubeRefreshToken = "youtube.refreshToken";
    public const string SpotifyClientId = "spotify.clientId";
    public const string SpotifyClientSecret = "spotify.clientSecret";
    public const string SpotifyAlbumsAndPlaylists = "spotify.albumsAndPlaylists";
    public const string LastfmApiKey = "lastfm.apiKey";
    public const string LastfmApiSecret = "lastfm.apiSecret";
    public const string SpeechEngine = "speech.engine";
    // "on": helpers play through their own voice connection via the relay, so the one playing can also listen.
    public const string VoiceRelay = "voice.relay";
    public const string SpeechLocalModel = "speech.localModel";
    public const string SpeechCloudUrl = "speech.cloudUrl";
    public const string SpeechCloudKey = "speech.cloudKey";
    public const string SpeechCloudModel = "speech.cloudModel";
    // The language model reading voice commands in members' own words (an OpenAI-style server's address).
    public const string UnderstandingUrl = "understanding.url";

    private readonly IDataProtector _protector = protection.CreateProtector("THOBOTTO.Integrations");
    private readonly ConcurrentDictionary<string, string> _values = new();

    // A key changed in the panel.
    public event Func<string, Task>? Changed;

    public string? Get(string key) => _values.GetValueOrDefault(key);

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
            _values[row.Key] = _protector.Unprotect(row.ProtectedValue);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
