using System.Collections.Concurrent;

using THOBOTTO.Voice;

namespace THOBOTTO.Music;

// A track as it was heard: who was listening (in the channel, not deafened) when it started.
public sealed record Listen(MusicPlayer Player, Track Track, DateTimeOffset StartedAt, IReadOnlySet<ulong> Listeners);

// How a listen ended: when, and who was still there.
public sealed record EndedListen(Listen Listen, DateTimeOffset EndedAt, IReadOnlySet<ulong> ListenersAtEnd)
{
    public TimeSpan Played => TimeSpan.FromMilliseconds(Math.Min((EndedAt - Listen.StartedAt).TotalMilliseconds, Listen.Track.LengthMs));
}

// Follows each player from track to track, for scrobbling and the play history.
public sealed class ListenTracker(VoicePresence presence, TimeProvider time)
{
    private readonly ConcurrentDictionary<MusicPlayer, Listen> _current = new();

    // The player moved on, or stopped (null): what just ended, and what just started.
    public (EndedListen? Ended, Listen? Started) Changed(MusicPlayer player, Track? next)
    {
        var now = time.GetUtcNow();
        var here = Listeners(player);
        var ended = _current.TryRemove(player, out var previous) ? new EndedListen(previous, now, here) : null;
        Listen? started = null;
        if (next is not null)
            _current[player] = started = new(player, next, now, here);
        return (ended, started);
    }

    private HashSet<ulong> Listeners(MusicPlayer player)
        => presence.Snapshot(player.GuildId).Where(p => !p.Value.IsBot && !p.Value.Deafened && player.Plays(p.Value.ChannelId)).Select(p => p.Key).ToHashSet();
}
