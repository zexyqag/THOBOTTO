using System.Collections.Concurrent;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Music;
using THOBOTTO.Voice;

namespace THOBOTTO.Lastfm;

// Scrobbles what the helpers play for the linked members listening (in the channel, not deafened):
// "now playing" when a track starts, a scrobble when it ends having played half its length or 4 minutes,
// for those who were there from the start.
public sealed class Scrobbler(
    LastfmClient client,
    IDbContextFactory<BotDbContext> dbFactory,
    IDataProtectionProvider protection,
    VoicePresence presence,
    TimeProvider time,
    ILogger<Scrobbler> logger)
{
    private static readonly TimeSpan Enough = TimeSpan.FromMinutes(4);

    private readonly IDataProtector _protector = protection.CreateProtector("THOBOTTO.LastfmSessions");
    private readonly ConcurrentDictionary<MusicPlayer, Listen> _listening = new();
    private readonly ConcurrentDictionary<ulong, LastfmLink?> _links = new();

    public bool Configured => client.Configured;

    // The player moved on, or stopped (null). Notes who listens now; Last.fm hears about it in the background.
    public void Changed(MusicPlayer player, Track? next)
    {
        if (!client.Configured)
            return;
        var now = time.GetUtcNow();
        var here = Listeners(player);
        var calls = new List<(ulong User, Func<string, Task> Call)>();

        if (_listening.TryRemove(player, out var previous) && now - previous.Track.StartedAt >= Min(previous.Length / 2, Enough))
            calls.AddRange(previous.Listeners.Where(here.Contains).Select(u => (u, (Func<string, Task>)(key => client.ScrobbleAsync(key, previous.Track)))));
        if (next is not null && TrackNames.From(next, now) is { } track)
        {
            _listening[player] = new(track, TimeSpan.FromMilliseconds(next.LengthMs), here);
            calls.AddRange(here.Select(u => (u, (Func<string, Task>)(key => client.NowPlayingAsync(key, track)))));
        }
        if (calls.Count > 0)
            _ = Task.Run(() => SendAllAsync(calls));
    }

    public async Task<LastfmLink?> FindAsync(ulong userId)
    {
        if (_links.TryGetValue(userId, out var known))
            return known;
        await using var db = await dbFactory.CreateDbContextAsync();
        return _links[userId] = await db.LastfmLinks.AsNoTracking().FirstOrDefaultAsync(l => l.UserId == userId);
    }

    public async Task LinkAsync(ulong userId, string username, string sessionKey)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var link = await db.LastfmLinks.FindAsync(userId);
        if (link is null)
            db.LastfmLinks.Add(link = new() { UserId = userId, Username = username, ProtectedSessionKey = "" });
        (link.Username, link.ProtectedSessionKey, link.Scrobbling, link.LinkedAt) = (username, _protector.Protect(sessionKey), true, time.GetUtcNow());
        await db.SaveChangesAsync();
        _links.TryRemove(userId, out _);
    }

    public async Task SetScrobblingAsync(ulong userId, bool on)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.LastfmLinks.Where(l => l.UserId == userId).ExecuteUpdateAsync(s => s.SetProperty(l => l.Scrobbling, on));
        _links.TryRemove(userId, out _);
    }

    public async Task UnlinkAsync(ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.LastfmLinks.Where(l => l.UserId == userId).ExecuteDeleteAsync();
        _links.TryRemove(userId, out _);
    }

    private HashSet<ulong> Listeners(MusicPlayer player)
        => presence.Snapshot(player.GuildId).Where(p => !p.Value.IsBot && !p.Value.Deafened && player.Plays(p.Value.ChannelId)).Select(p => p.Key).ToHashSet();

    private async Task SendAllAsync(IEnumerable<(ulong User, Func<string, Task> Call)> calls)
    {
        foreach (var (user, call) in calls)
        {
            if (await FindAsync(user) is not { Scrobbling: true } link)
                continue;
            try
            {
                await call(_protector.Unprotect(link.ProtectedSessionKey));
            }
            catch (LastfmException ex) when (ex.SessionInvalid)
            {
                logger.LogInformation("Last.fm access for {UserId} was revoked; unlinking", user);
                await UnlinkAsync(user);
            }
            catch (Exception ex) when (ex is LastfmException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                logger.LogWarning("Last.fm for {UserId}: {Message}", user, ex.Message);
            }
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private sealed record Listen(Scrobble Track, TimeSpan Length, HashSet<ulong> Listeners);
}
