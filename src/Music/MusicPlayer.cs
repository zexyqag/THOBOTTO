using System.Text.Json.Nodes;

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

    // Reports in a row that were out of step; one alone may be a glitch.
    public int Strikes { get; set; }
}

// One helper's queue in one guild. Lavalink plays a track at a time; when one ends, the next goes.
// Mirrors play along: every change goes to them too, and the leader's track ends drive the queue.
public sealed class MusicPlayer(HelperBot helper, ulong guildId, ulong voiceChannelId, ulong textChannelId, TimeProvider time)
{
    // Further apart than this (ms), a mirror seeks to where the leader is.
    private const long DriftTolerance = 300;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<Track> _queue = [];
    private IReadOnlyList<Mirror> _mirrors = [];

    // Where the leader is, kept as when (Lavalink's clock, unix ms) its track would have started at
    // normal speed: report time minus position. Its last few reports, as one may be off; until it
    // reports, a guess from when the track was sent or resumed.
    private readonly List<long> _leaderStarts = [];
    private long _guessedStart;
    private long _pausedAt;

    // Reports from before the track started or resumed are about the old state.
    private long _playingSince;

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
            _pausedAt = PositionNow();
        else
            (_guessedStart, _playingSince) = (Now - _pausedAt, Now);
        _leaderStarts.Clear();
        Paused = paused;
        await SendAsync(() => new() { ["paused"] = paused });
    });

    public Task SetVolumeAsync(int volume) => WithGate(async () =>
    {
        Volume = volume;
        await SendAsync(() => new() { ["volume"] = volume });
    });

    // Starts the mirror where the leader is.
    public Task AddMirrorAsync(Mirror mirror) => WithGate(async () =>
    {
        _mirrors = [.. _mirrors, mirror];
        if (Current is not null)
            await SendToMirrorAsync(mirror, TrackBody(Current, PositionNow()));
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
            if (Current is null || Paused || at < _playingSince)
                return null;
            if (from == helper)
            {
                _leaderStarts.Add(at - position);
                if (_leaderStarts.Count > 3)
                    _leaderStarts.RemoveAt(0);
                return null;
            }
            if (_leaderStarts.Count == 0 || _mirrors.FirstOrDefault(m => m.Helper == from) is not { } mirror)
                return null;

            var drift = LeaderStart - (at - position);
            if (Math.Abs(drift) <= DriftTolerance)
                mirror.Strikes = 0;
            else if (++mirror.Strikes >= 2)
            {
                mirror.Strikes = 0;
                await SendToMirrorAsync(mirror, new() { ["position"] = PositionNow() });
                return drift;
            }
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

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

    private async Task PlayNextCoreAsync(bool skipping)
    {
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
            _playingSince = _guessedStart = Now;
            _leaderStarts.Clear();
            await SendAsync(() => TrackBody(next, 0));
        }
        await RaiseAsync(next);
    }

    private long Now => time.GetUtcNow().ToUnixTimeMilliseconds();

    private long LeaderStart => _leaderStarts.Count == 0 ? _guessedStart : _leaderStarts.Order().ElementAt(_leaderStarts.Count / 2);

    private long PositionNow() => Paused ? _pausedAt : Now - LeaderStart;

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
