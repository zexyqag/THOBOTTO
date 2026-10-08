using System.Text.Json.Nodes;

namespace THOBOTTO.Music;

public enum LoopMode
{
    Off,
    Track,
    Queue,
}

// One helper's queue in one guild. Lavalink plays a track at a time; when one ends, the next goes.
public sealed class MusicPlayer(HelperBot helper, ulong guildId, ulong voiceChannelId, ulong textChannelId, TimeProvider time)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<Track> _queue = [];

    public HelperBot Helper => helper;

    public ulong GuildId => guildId;

    public ulong VoiceChannelId { get; } = voiceChannelId;

    public ulong TextChannelId { get; set; } = textChannelId;

    public Track? Current { get; private set; }

    public IReadOnlyList<Track> Queue => _queue;

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
        Paused = paused;
        await helper.Lavalink.UpdatePlayerAsync(guildId, new JsonObject { ["paused"] = paused });
    });

    public Task SetVolumeAsync(int volume) => WithGate(async () =>
    {
        Volume = volume;
        await helper.Lavalink.UpdatePlayerAsync(guildId, new JsonObject { ["volume"] = volume });
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
        await helper.Lavalink.UpdatePlayerAsync(guildId, new JsonObject { ["track"] = new JsonObject { ["encoded"] = null } });
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
            await helper.Lavalink.UpdatePlayerAsync(guildId, new JsonObject { ["track"] = new JsonObject { ["encoded"] = null } });
        }
        else
        {
            await helper.Lavalink.UpdatePlayerAsync(guildId, new JsonObject
            {
                ["track"] = new JsonObject { ["encoded"] = next.Encoded },
                ["volume"] = Volume,
                ["paused"] = false,
            });
        }
        await RaiseAsync(next);
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
