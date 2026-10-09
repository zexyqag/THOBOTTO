using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using THOBOTTO.Helpers;

namespace THOBOTTO.Music;

public enum LoopMode
{
    Off,
    Track,
    Queue,
}

// Another helper playing the same queue in another voice channel.
public sealed class Mirror(HelperBot helper, ulong voiceChannelId)
{
    public HelperBot Helper => helper;

    public ulong VoiceChannelId => voiceChannelId;

    public DateTimeOffset? LonelySince { get; set; }
}

// One helper's queue in one guild. Lavalink plays a track at a time; when one ends, the next goes.
// Mirrors play along: every change goes to them too, and the leader's track ends drive the queue.
public sealed class MusicPlayer(HelperBot helper, ulong guildId, ulong voiceChannelId, ulong textChannelId, TimeProvider time)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<Track> _queue = [];
    private readonly DriftTracker _drift = new();
    private IReadOnlyList<Mirror> _mirrors = [];

    public HelperBot Helper => helper;

    public ulong GuildId => guildId;

    public ulong VoiceChannelId { get; } = voiceChannelId;

    public ulong TextChannelId { get; set; } = textChannelId;

    public Track? Current { get; private set; }

    public IReadOnlyList<Track> Queue => _queue;

    public IReadOnlyList<Mirror> Mirrors => _mirrors;

    public IEnumerable<ulong> Channels => [VoiceChannelId, .. _mirrors.Select(m => m.VoiceChannelId)];

    public bool Plays(ulong channelId) => Channels.Contains(channelId);

    public ulong ChannelOf(HelperBot bot) => _mirrors.FirstOrDefault(m => m.Helper == bot)?.VoiceChannelId ?? VoiceChannelId;

    public LoopMode Loop { get; set; }

    // When the queue runs out, the service adds songs like the last one.
    public bool Autoplay { get; set; }

    public int Volume { get; private set; } = 100;

    public bool Paused { get; private set; }

    // For leaving when nothing has played for a while.
    public DateTimeOffset IdleSince { get; private set; } = time.GetUtcNow();

    public ulong? NowPlayingMessageId { get; set; }

    // Whether the helper posted it (else the main bot did, for it).
    public bool NowPlayingByHelper { get; set; }

    // Since when nobody listens or nothing plays; the service sends the helper home after a while.
    public DateTimeOffset? LonelySince { get; set; }

    // Raised when a new track starts (or the queue runs out: null), to post "now playing".
    public event Func<MusicPlayer, Track?, Task>? Changed;

    // Raised (inside the player's lock) when the last track ended or was skipped with nothing queued.
    public event Func<MusicPlayer, Track, Task>? RanOut;

    // Recently played, so autoplay doesn't come back to them.
    private readonly Queue<string> _played = new();
    private const int PlayedRemembered = 100;

    // Who voted to skip the current track.
    private readonly HashSet<ulong> _skipVotes = [];

    public bool HasPlayed(Track track) => _played.Contains(PlayedKey(track));

    private static string PlayedKey(Track track) => $"{track.Title}\n{track.Author}".ToLowerInvariant();

    // Returns the position the first track got: 0 means it plays right away.
    public async Task<int> EnqueueAsync(IReadOnlyList<Track> tracks)
    {
        await _gate.WaitAsync();
        try
        {
            var position = Current is null ? 0 : _queue.Count + 1;
            _queue.AddRange(tracks);
            if (Current is null)
                await PlayNextCoreAsync(skipping: false);
            return position;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SkipAsync() => WithGate(() => PlayNextCoreAsync(skipping: true));

    // Called when Lavalink reports the track ended on its own.
    public Task TrackEndedAsync(string? reason)
        => reason is "finished" or "loadFailed" ? WithGate(() => PlayNextCoreAsync(skipping: false)) : Task.CompletedTask;

    public Task SetPausedAsync(bool paused) => WithGate(async () =>
    {
        if (paused == Paused)
            return;
        if (paused)
            _drift.Pause(Now);
        else
            _drift.Resume(Now);
        Paused = paused;
        await SendAsync(() => new() { ["paused"] = paused });
    });

    public Task SetVolumeAsync(int volume) => WithGate(async () =>
    {
        Volume = volume;
        await SendAsync(() => new() { ["volume"] = volume });
        // Channels lowered while people talk stay lowered, at the new volume.
        foreach (var (helperId, percent) in _ducked)
            await SendToAsync(helperId, new() { ["volume"] = volume * percent / 100 });
    });

    // Helper → the percent its channel is lowered to while people there talk.
    private readonly ConcurrentDictionary<ulong, int> _ducked = new();

    // Lowers (or, with null, restores) the music in one helper's channel only.
    public Task DuckAsync(HelperBot bot, int? percent) => WithGate(async () =>
    {
        if (percent is { } lowered)
            _ducked[bot.UserId] = lowered;
        else if (!_ducked.TryRemove(bot.UserId, out _))
            return;
        await SendToAsync(bot.UserId, new() { ["volume"] = percent is { } p ? Volume * p / 100 : Volume });
    });

    public bool IsDucked(HelperBot bot) => _ducked.ContainsKey(bot.UserId);

    private Task SendToAsync(ulong helperId, JsonObject body)
        => helperId == helper.UserId ? helper.Lavalink.UpdatePlayerAsync(guildId, body)
            : _mirrors.FirstOrDefault(m => m.Helper.UserId == helperId) is { } mirror ? SendToMirrorAsync(mirror, body) : Task.CompletedTask;

    // Starts the mirror where the leader is.
    public Task AddMirrorAsync(Mirror mirror) => WithGate(async () =>
    {
        _mirrors = [.. _mirrors, mirror];
        if (Current is not null)
            await SendToMirrorAsync(mirror, TrackBody(Current, _drift.Position(Now)));
    });

    public Task<PlayerState> SaveAsync() => WithGate(() => Task.FromResult(new PlayerState(
        helper.UserId, VoiceChannelId, TextChannelId,
        _mirrors.Select(m => new MirrorState(m.Helper.UserId, m.VoiceChannelId)).ToList(),
        Current, Position, Paused, Loop, Volume, _queue.ToList(), NowPlayingMessageId, NowPlayingByHelper, Autoplay)));

    // Back to how it was saved: the track goes on where it was, the queue and settings as they were.
    public Task RestoreAsync(PlayerState state) => WithGate(async () =>
    {
        _queue.Clear();
        _queue.AddRange(state.Queue);
        (Loop, Volume, Paused, Autoplay) = (state.Loop, state.Volume, state.Paused, state.Autoplay);
        (NowPlayingMessageId, NowPlayingByHelper) = (state.NowPlayingMessageId, state.NowPlayingByHelper);
        Current = state.Current;
        if (Current is null)
            return;
        _drift.Start(Now, state.Position, state.Paused);
        await SendAsync(() => TrackBody(Current, state.Position));
        await RaiseAsync(Current);
    });

    // The helper's Lavalink restarted: the current track goes on from where it would be by now.
    public Task ResumeAsync(HelperBot bot) => WithGate(async () =>
    {
        if (Current is null)
            return;
        var body = TrackBody(Current, _drift.Position(Now));
        if (bot == helper)
            await helper.Lavalink.UpdatePlayerAsync(guildId, body);
        else if (_mirrors.FirstOrDefault(m => m.Helper == bot) is { } mirror)
            await SendToMirrorAsync(mirror, body);
    });

    public Task RemoveMirrorAsync(Mirror mirror) => WithGate(() =>
    {
        _mirrors = _mirrors.Where(m => m != mirror).ToList();
        return Task.CompletedTask;
    });

    // Lavalink reports each player's position every few seconds; a mirror that drifted seeks back.
    // Returns how far off (ms) a corrected mirror was.
    public async Task<long?> PositionAsync(HelperBot from, long at, long position)
    {
        await _gate.WaitAsync();
        try
        {
            if (Current is null)
                return null;
            if (from == helper)
            {
                _drift.Leader(at, position);
                return null;
            }
            if (_mirrors.FirstOrDefault(m => m.Helper == from) is not { } mirror || _drift.Mirror(from.UserId, at, position) is not { } drift)
                return null;

            await SendToMirrorAsync(mirror, new() { ["position"] = _drift.Position(Now) });
            return drift;
        }
        finally
        {
            _gate.Release();
        }
    }

    // How far into the current track it is now (ms).
    public long Position => Current is null ? 0 : Math.Min(_drift.Position(Now), Current.LengthMs);

    // Queue edits name the track as well as its place, so an edit from a page loaded before the queue moved on misses.
    public Task<bool> RemoveAsync(int index, string encoded) => WithGate(() =>
    {
        if (index < 0 || index >= _queue.Count || _queue[index].Encoded != encoded)
            return Task.FromResult(false);
        _queue.RemoveAt(index);
        return Task.FromResult(true);
    });

    public Task<bool> PlayNextAsync(int index, string encoded) => WithGate(() =>
    {
        if (index < 0 || index >= _queue.Count || _queue[index].Encoded != encoded)
            return Task.FromResult(false);
        var track = _queue[index];
        _queue.RemoveAt(index);
        _queue.Insert(0, track);
        return Task.FromResult(true);
    });

    public Task ShuffleAsync() => WithGate(() =>
    {
        var shuffled = _queue.OrderBy(_ => Random.Shared.Next()).ToList();
        _queue.Clear();
        _queue.AddRange(shuffled);
        return Task.CompletedTask;
    });

    public Task StopAsync() => WithGate(async () =>
    {
        _queue.Clear();
        Current = null;
        Paused = false;
        IdleSince = time.GetUtcNow();
        await SendAsync(() => new() { ["track"] = new JsonObject { ["encoded"] = null } });
        await RaiseAsync(null);
    });

    // Counts the vote for the current track; returns everyone who voted for it so far.
    public Task<IReadOnlySet<ulong>> VoteSkipAsync(ulong userId) => WithGate(() =>
    {
        if (Current is not null)
            _skipVotes.Add(userId);
        return Task.FromResult<IReadOnlySet<ulong>>(_skipVotes.ToHashSet());
    });

    private async Task PlayNextCoreAsync(bool skipping)
    {
        var previous = Current;
        _skipVotes.Clear();
        // Looping one track repeats it, unless someone skips; looping the queue sends it to the back.
        Track? next;
        if (Loop == LoopMode.Track && !skipping && Current is not null)
            next = Current;
        else
        {
            if (Loop == LoopMode.Queue && Current is not null)
                _queue.Add(Current);
            next = _queue.Count > 0 ? _queue[0] : null;
            if (next is not null)
                _queue.RemoveAt(0);
        }

        Current = next;
        Paused = false;
        if (next is null)
        {
            IdleSince = time.GetUtcNow();
            await SendAsync(() => new() { ["track"] = new JsonObject { ["encoded"] = null } });
        }
        else
        {
            _played.Enqueue(PlayedKey(next));
            if (_played.Count > PlayedRemembered)
                _played.Dequeue();
            _drift.Start(Now);
            await SendAsync(() => TrackBody(next, 0));
        }
        await RaiseAsync(next);
        if (next is null && previous is not null && RanOut is { } ranOut)
            await ranOut(this, previous);
    }

    private long Now => time.GetUtcNow().ToUnixTimeMilliseconds();


    private JsonObject TrackBody(Track track, long position) => new()
    {
        ["track"] = new JsonObject { ["encoded"] = track.Encoded },
        ["position"] = position,
        ["volume"] = Volume,
        ["paused"] = Paused,
    };

    // To the leader and every mirror; a mirror that fails is left to its own disconnect handling.
    private Task SendAsync(Func<JsonObject> body)
        => Task.WhenAll(_mirrors.Select(m => SendToMirrorAsync(m, body())).Append(helper.Lavalink.UpdatePlayerAsync(guildId, body())));

    private async Task SendToMirrorAsync(Mirror mirror, JsonObject body)
    {
        try
        {
            await mirror.Helper.Lavalink.UpdatePlayerAsync(guildId, body);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
        }
    }

    private async Task RaiseAsync(Track? track)
    {
        if (Changed is { } changed)
            await changed(this, track);
    }

    private async Task<T> WithGate<T>(Func<Task<T>> action)
    {
        await _gate.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WithGate(Func<Task> action)
    {
        await _gate.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            _gate.Release();
        }
    }
}
