using System.Collections.Concurrent;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Music;

namespace THOBOTTO.Lastfm;

// Scrobbles what the helpers play for the linked members listening (in the channel, not deafened):
// "now playing" when a track starts, a scrobble when it ends having played half its length or 4 minutes,
// for those who were there from the start.
public sealed class Scrobbler(
    LastfmClient client,
    IDbContextFactory<BotDbContext> dbFactory,
    IDataProtectionProvider protection,
    TimeProvider time,
    ILogger<Scrobbler> logger)
{
    private static readonly TimeSpan Enough = TimeSpan.FromMinutes(4);

    private readonly IDataProtector _protector = protection.CreateProtector("THOBOTTO.LastfmSessions");
    private readonly ConcurrentDictionary<ulong, LastfmLink?> _links = new();

    public bool Configured => client.Configured;

    // A listen ended and/or another started. Last.fm hears about it in the background.
    public void Record(EndedListen? ended, Listen? started)
    {
        if (!client.Configured)
            return;
        var calls = new List<(ulong User, Func<string, Task> Call)>();
        if (ended is { Failed: false, Listen: var previous } && TrackNames.From(previous.Track, previous.StartedAt) is { } heard
            && ended.Played >= Min(TimeSpan.FromMilliseconds(previous.Track.LengthMs / 2), Enough))
            calls.AddRange(previous.Listeners.Where(ended.ListenersAtEnd.Contains).Select(u => (u, (Func<string, Task>)(key => client.ScrobbleAsync(key, heard)))));
        if (started is not null && TrackNames.From(started.Track, started.StartedAt) is { } playing)
            calls.AddRange(started.Listeners.Select(u => (u, (Func<string, Task>)(key => client.NowPlayingAsync(key, playing)))));
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

}
