using System.Collections.Concurrent;

using THOBOTTO.Voice;

namespace THOBOTTO.Music;

// A track as it was heard: who was listening (in the channel, not deafened) when it started.
public sealed record Listen(MusicPlayer Player, Track Track, DateTimeOffset StartedAt, IReadOnlySet<ulong> Listeners);

// How a listen ended: when, who was still there, and whether the track failed to play.
public sealed record EndedListen(Listen Listen, DateTimeOffset EndedAt, IReadOnlySet<ulong> ListenersAtEnd, bool Failed)
{
    public TimeSpan Played => TimeSpan.FromMilliseconds(Math.Min((EndedAt - Listen.StartedAt).TotalMilliseconds, Listen.Track.LengthMs));
}

// Follows each player from track to track, for scrobbling and the play history.
public sealed class ListenTracker(VoicePresence presence, TimeProvider time)
{
    private readonly ConcurrentDictionary<MusicPlayer, Listen> _current = new();
    private readonly ConcurrentDictionary<MusicPlayer, Listen> _failed = new();

    // Lavalink couldn't play the current track; it isn't counted as heard.
    public void Failed(MusicPlayer player)
    {
        if (_current.TryGetValue(player, out var listen))
            _failed[player] = listen;
    }

    // The player moved on, or stopped (null): what just ended, and what just started.
    public (EndedListen? Ended, Listen? Started) Changed(MusicPlayer player, Track? next)
    {
        var now = time.GetUtcNow();
        var here = Listeners(player);
        var failed = _failed.TryRemove(player, out var broken);
        var ended = _current.TryRemove(player, out var previous) ? new EndedListen(previous, now, here, failed && broken == previous) : null;
        Listen? started = null;
        if (next is not null)
            _current[player] = started = new(player, next, now, here);
        return (ended, started);
    }

    private HashSet<ulong> Listeners(MusicPlayer player)
        => presence.Snapshot(player.GuildId).Where(p => !p.Value.IsBot && !p.Value.Deafened && player.Plays(p.Value.ChannelId)).Select(p => p.Key).ToHashSet();
}
