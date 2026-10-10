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

// Where added songs go: playing now (the current one skipped), first, or last.
public enum Placement
{
    Now,
    First,
    Last,
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
// The queue has two parts, as Spotify's: songs people added, then playlists and autoplay's picks.
public sealed class MusicPlayer(HelperBot helper, ulong guildId, ulong voiceChannelId, ulong textChannelId, TimeProvider time)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<Track> _added = [];
    private readonly List<Track> _later = [];
    private readonly DriftTracker _drift = new();
    private IReadOnlyList<Mirror> _mirrors = [];

    public HelperBot Helper => helper;

    public ulong GuildId => guildId;

    public ulong VoiceChannelId { get; } = voiceChannelId;

    public ulong TextChannelId { get; set; } = textChannelId;

    public Track? Current { get; private set; }

    // Everything coming up: the added songs, then the rest.
    public IReadOnlyList<Track> Queue => [.. _added, .. _later];

    public int AddedCount => _added.Count;

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

    // A playlist (or autoplay's picks) goes in the second part: playing now replaces what's there. Returns the
    // position the first track got: 0 means it plays right away.
    public async Task<int> AddAsync(IReadOnlyList<Track> tracks, bool playlist, Placement placement)
    {
        await _gate.WaitAsync();
        try
        {
            var part = playlist ? _later : _added;
            if (placement == Placement.Now)
            {
                if (playlist)
                    _later.Clear();
                part.InsertRange(0, tracks.Skip(1));
                await StartCoreAsync(tracks[0]);
                return 0;
            }
            var position = placement == Placement.First ? (playlist ? _added.Count : 0) + 1 : (playlist ? _added.Count + _later.Count : _added.Count) + 1;
            part.InsertRange(placement == Placement.First ? 0 : part.Count, tracks);
            if (Current is not null)
                return position;
            await PlayNextCoreAsync(skipping: false);
            return 0;
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

    // Helper → the percent of the volume its channel plays at while lowered for people talking (or coming back).
    private readonly ConcurrentDictionary<ulong, int> _ducked = new();
    // How far one step of a fade goes, in percent: quickly down, gently back up.
    private const int FadeDown = 30;
    private const int FadeUp = 10;

    public int LevelOf(HelperBot bot) => _ducked.GetValueOrDefault(bot.UserId, 100);

    // One step toward a level (percent of the volume) in one helper's channel only.
    public Task FadeAsync(HelperBot bot, int target) => WithGate(async () =>
    {
        var level = LevelOf(bot);
        if (level == target)
            return;
        var next = target < level ? Math.Max(target, level - FadeDown) : Math.Min(target, level + FadeUp);
        if (next >= 100)
            _ducked.TryRemove(bot.UserId, out _);
        else
            _ducked[bot.UserId] = next;
        await SendToAsync(bot.UserId, new() { ["volume"] = Volume * next / 100 });
    });

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
        Current, Position, Paused, Loop, Volume, _added.ToList(), NowPlayingMessageId, NowPlayingByHelper, Autoplay, _later.ToList())));

    // Back to how it was saved: the track goes on where it was, the queue and settings as they were.
    public Task RestoreAsync(PlayerState state) => WithGate(async () =>
    {
        _added.Clear();
        _later.Clear();
        _added.AddRange(state.Queue);
        _later.AddRange(state.Later ?? []);
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
    public Task<bool> RemoveAsync(int index, string encoded) => WithGate(() => Task.FromResult(TakeAt(index, encoded) is not null));

    // To the top of the added songs.
    public Task<bool> PlayNextAsync(int index, string encoded) => WithGate(() =>
    {
        if (TakeAt(index, encoded) is not { } track)
            return Task.FromResult(false);
        _added.Insert(0, track);
        return Task.FromResult(true);
    });

    // Index into Queue.
    private Track? TakeAt(int index, string encoded)
    {
        var (part, at) = index < _added.Count ? (_added, index) : (_later, index - _added.Count);
        if (at < 0 || at >= part.Count || part[at].Encoded != encoded)
            return null;
        var track = part[at];
        part.RemoveAt(at);
        return track;
    }

    // Each part on its own.
    public Task ShuffleAsync() => WithGate(() =>
    {
        foreach (var part in new[] { _added, _later })
        {
            var shuffled = part.OrderBy(_ => Random.Shared.Next()).ToList();
            part.Clear();
            part.AddRange(shuffled);
        }
        return Task.CompletedTask;
    });

    public Task StopAsync() => WithGate(async () =>
    {
        _added.Clear();
        _later.Clear();
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
        // Looping one track repeats it, unless someone skips; looping the queue sends it to the back.
        if (Loop == LoopMode.Track && !skipping && Current is not null)
        {
            await StartCoreAsync(Current);
            return;
        }
        if (Loop == LoopMode.Queue && Current is not null)
            _later.Add(Current);
        var part = _added.Count > 0 ? _added : _later;
        Track? next = null;
        if (part.Count > 0)
        {
            next = part[0];
            part.RemoveAt(0);
        }
        await StartCoreAsync(next);
    }

    private async Task StartCoreAsync(Track? next)
    {
        var previous = Current;
        _skipVotes.Clear();
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
