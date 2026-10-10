using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Access;
using THOBOTTO.Modules;
using THOBOTTO.Voice;

namespace THOBOTTO.Watch;

public sealed record WatchItem(Video Video, ulong AddedBy, string? StreamId);

// A voice channel watching together: what plays, the queue, and one timeline everyone follows (where it was at
// a moment, and whether it's moving).
public sealed class WatchRoom(ulong guildId, ulong channelId)
{
    public ulong GuildId => guildId;

    public ulong ChannelId => channelId;

    public List<WatchItem> Queue { get; } = [];

    public WatchItem? Current { get; set; }

    public bool Paused { get; set; }

    public double AnchorSeconds { get; set; }

    public DateTimeOffset AnchorAt { get; set; }

    public DateTimeOffset? EmptySince { get; set; }

    public SemaphoreSlim Gate { get; } = new(1, 1);

    public double Position(DateTimeOffset now) => Paused ? AnchorSeconds : AnchorSeconds + Math.Max(0, (now - AnchorAt).TotalSeconds);
}

// Watching videos together, a room per voice channel, played in everyone's Activity in step: added with /watch or
// in the Activity, moved on when a video ends, closed a while after the channel empties.
public sealed class WatchRooms(
    VideoResolver resolver,
    HlsStreams streams,
    SettingsStore settings,
    ModuleState modules,
    AccessControl access,
    VoicePresence presence,
    RestClient rest,
    TimeProvider time,
    ILogger<WatchRooms> logger) : BackgroundService
{
    public const string ModuleId = "watch";
    // A moment for ffmpeg to have the first segments before the timeline starts moving.
    private static readonly TimeSpan Lead = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan EmptyFor = TimeSpan.FromMinutes(5);
    private const int QueueShown = 20;

    private readonly ConcurrentDictionary<(ulong, ulong), WatchRoom> _rooms = new();

    public ValueTask<WatchRules> RulesAsync(ulong guildId) => settings.GetAsync<WatchRules>(guildId, ModuleId);

    // Adds a video (a link or search words); the answer.
    public async Task<string> AddAsync(ulong guildId, ulong channelId, ulong userId, string query)
    {
        if (!await modules.IsEnabledAsync(guildId, ModuleId))
            return $"The `{ModuleId}` module is off.";
        var rules = await RulesAsync(guildId);
        var (video, problem) = await resolver.ResolveAsync(query, int.Parse(rules.Quality));
        if (video is null)
            return problem!;
        if (video.Seconds > rules.MaxMinutes * 60)
            return $"That's {video.Seconds / 60:0} minutes; videos here can be at most {rules.MaxMinutes}.";
        var room = _rooms.GetOrAdd((guildId, channelId), _ => new(guildId, channelId));
        await room.Gate.WaitAsync();
        try
        {
            if (room.Current is not null)
            {
                room.Queue.Add(new(video, userId, null));
                return $"🎬 Queued **{video.Title}** (#{room.Queue.Count}).";
            }
            return Start(room, new(video, userId, null)) ? $"🎬 **{video.Title}**" : "Videos can't be played here right now (ffmpeg is missing).";
        }
        finally
        {
            room.Gate.Release();
        }
    }

    // A control from someone watching; the answer.
    public async Task<string> ControlAsync(Guild guild, GuildUser member, ulong channelId, string action, double? value)
    {
        if (!_rooms.TryGetValue((guild.Id, channelId), out var room) || room.Current is null)
            return "Nothing is being watched here.";
        if ((await RulesAsync(guild.Id)).Control == WatchControl.Djs && !await access.CanAsync(guild, member, BotPermissions.MusicDj))
            return $"Controlling playback needs `{BotPermissions.MusicDj}` here.";
        await room.Gate.WaitAsync();
        try
        {
            var now = time.GetUtcNow();
            switch (action)
            {
                case "play" or "pause":
                    (room.AnchorSeconds, room.AnchorAt, room.Paused) = (room.Position(now), now, action == "pause");
                    return action == "pause" ? "⏸️ Paused." : "▶️ Playing.";
                case "seek" when value is { } seconds:
                    (room.AnchorSeconds, room.AnchorAt) = (Math.Clamp(seconds, 0, room.Current.Video.Seconds ?? seconds), now);
                    return $"⏩ {TimeSpan.FromSeconds(room.AnchorSeconds):m\\:ss}";
                case "skip":
                    Next(room);
                    return room.Current is { } next ? $"⏭️ {next.Video.Title}" : "⏹️ That was the last one.";
                case "stop":
                    Close(room);
                    return "⏹️ Stopped.";
                default:
                    return "That isn't something I can do.";
            }
        }
        finally
        {
            room.Gate.Release();
        }
    }

    // What the Activity shows; null when nothing is being watched there.
    public JsonObject? State(ulong guildId, ulong channelId)
    {
        if (!_rooms.TryGetValue((guildId, channelId), out var room) || room.Current is not { } current)
            return null;
        var now = time.GetUtcNow();
        return new JsonObject
        {
            ["title"] = current.Video.Title,
            ["uploader"] = current.Video.Uploader,
            ["page"] = current.Video.Page,
            ["seconds"] = current.Video.Seconds,
            ["stream"] = current.StreamId,
            ["paused"] = room.Paused,
            // Where it is now; before the start (the lead), a negative wait.
            ["position"] = room.Paused ? room.AnchorSeconds : room.AnchorSeconds + (now - room.AnchorAt).TotalSeconds,
            ["queue"] = new JsonArray([.. room.Queue.Take(QueueShown).Select(i => (JsonNode)new JsonObject { ["title"] = i.Video.Title, ["seconds"] = i.Video.Seconds })]),
        };
    }

    private bool Start(WatchRoom room, WatchItem item)
    {
        if (streams.Start(item.Video) is not { } id)
            return false;
        room.Current = item with { StreamId = id };
        (room.AnchorSeconds, room.AnchorAt, room.Paused) = (0, time.GetUtcNow() + Lead, false);
        return true;
    }

    private void Next(WatchRoom room)
    {
        if (room.Current?.StreamId is { } old)
            streams.Stop(old);
        room.Current = null;
        while (room.Queue.Count > 0 && room.Current is null)
        {
            var next = room.Queue[0];
            room.Queue.RemoveAt(0);
            Start(room, next);
        }
    }

    private void Close(WatchRoom room)
    {
        room.Queue.Clear();
        Next(room);
        _rooms.TryRemove((room.GuildId, room.ChannelId), out _);
    }

    // Videos that ended go on to the next; rooms whose channel stayed empty close; streams nobody uses go.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var now = time.GetUtcNow();
                foreach (var room in _rooms.Values.ToList())
                {
                    await room.Gate.WaitAsync(stoppingToken);
                    try
                    {
                        if (room.Current?.Video.Seconds is { } length && !room.Paused && room.Position(now) >= length + 1)
                            Next(room);
                        var someone = presence.Snapshot(room.GuildId).Any(p => p.Value.ChannelId == room.ChannelId && !p.Value.IsBot);
                        room.EmptySince = someone ? null : room.EmptySince ?? now;
                        if (room.Current is null && room.Queue.Count == 0 || room.EmptySince is { } since && now - since > EmptyFor)
                            Close(room);
                    }
                    finally
                    {
                        room.Gate.Release();
                    }
                }
                streams.Sweep(_rooms.Values.Select(r => r.Current?.StreamId).OfType<string>().ToHashSet());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Watching: moving on failed");
            }
        }
    }

    // For /watch: says what came of adding in the voice channel's chat.
    public async Task AnnounceAsync(ulong channelId, string text)
    {
        try
        {
            await rest.SendMessageAsync(channelId, new() { Content = text, AllowedMentions = AllowedMentionsProperties.None });
        }
        catch (RestException ex)
        {
            logger.LogDebug("Announcing in {ChannelId}: {Message}", channelId, ex.Message);
        }
    }

    public override void Dispose()
    {
        streams.Dispose();
        base.Dispose();
    }
}
