using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Access;
using THOBOTTO.Debugging;
using THOBOTTO.Discord;
using THOBOTTO.Modules;
using THOBOTTO.Music;
using THOBOTTO.Points;

namespace THOBOTTO.Activity.Watch;

public sealed record WatchItem(Video Video, ulong AddedBy, string? StreamId)
{
    // Who voted for it while queued.
    public HashSet<ulong> Votes { get; } = [];
}

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

    // Why the last video couldn't be streamed, shown a little while.
    public (string Text, DateTimeOffset At)? Problem { get; set; }

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
    PointsEngine points,
    TimeProvider time,
    ILogger<WatchRooms> logger) : BackgroundService
{
    public const string ModuleId = "watch";
    // A moment for ffmpeg to have the first segments before the timeline starts moving.
    private static readonly TimeSpan Lead = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan EmptyFor = TimeSpan.FromMinutes(5);
    private const int QueueShown = 20;
    private static readonly TimeSpan ProblemShown = TimeSpan.FromSeconds(30);
    // Any member may add, so a server's share of rooms, lookups and adds is bounded.
    private const int RoomsPerGuild = 3;
    private const int ResolvesPerGuild = 1;
    private const int AddsPerMemberPerMinute = 3;

    private readonly ConcurrentDictionary<(ulong, ulong), WatchRoom> _rooms = new();
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _resolving = new();
    private readonly ConcurrentDictionary<(ulong, ulong), List<DateTimeOffset>> _adds = new();

    public ValueTask<WatchRules> RulesAsync(ulong guildId) => settings.GetAsync<WatchRules>(guildId, ModuleId);

    // Adds a video (a link or search words); the answer.
    public async Task<string> AddAsync(Guild guild, GuildUser member, ulong channelId, string query)
    {
        // The lookup's lines (the resolver's) are this server's.
        using var _ = logger.BeginScope(DebugLog.Guild(guild.Id));
        if (!await modules.IsEnabledAsync(guild.Id, ModuleId))
            return ModuleRegistry.Off(ModuleId);
        var rules = await RulesAsync(guild.Id);
        if (rules.Adding == WatchControl.Djs && !await access.CanAsync(guild, member, BotPermissions.MusicDj))
            return $"Adding videos needs `{BotPermissions.MusicDj}` here.";
        if (!_rooms.ContainsKey((guild.Id, channelId)) && _rooms.Values.Count(r => r.GuildId == guild.Id) >= RoomsPerGuild)
            return $"{RoomsPerGuild} channels are already watching here; wait for one to finish.";
        var resolving = _resolving.GetOrAdd(guild.Id, _ => new(ResolvesPerGuild, ResolvesPerGuild));
        if (!await resolving.WaitAsync(0))
            return "Another video is being looked up here; try again in a moment.";
        Video? video;
        string? problem;
        try
        {
            if (!TakeAdd(guild.Id, member.Id))
                return $"That's {AddsPerMemberPerMinute} videos within a minute; wait a little.";
            (video, problem) = await resolver.ResolveAsync(query, int.Parse(rules.Quality));
        }
        finally
        {
            resolving.Release();
        }
        if (video is null)
            return problem!;
        if (video.Seconds > rules.MaxMinutes * 60)
            return $"That's {video.Seconds / 60:0} minutes; videos here can be at most {rules.MaxMinutes}.";
        var room = _rooms.GetOrAdd((guild.Id, channelId), _ => new(guild.Id, channelId));
        await points.InteractAsync(guild.Id, member.Id, InteractionKind.Watch, $"video {video.Id}");
        await room.Gate.WaitAsync();
        try
        {
            if (room.Current is not null)
            {
                room.Queue.Add(new(video, member.Id, null));
                return $"🎬 Queued **{video.Title}** (#{room.Queue.Count}).";
            }
            return Start(room, new(video, member.Id, null)) ? $"🎬 **{video.Title}**" : "Videos can't be played here right now; why is in the bot's log.";
        }
        finally
        {
            room.Gate.Release();
        }
    }

    // One more add for the member now, unless they've had their share this minute.
    private bool TakeAdd(ulong guildId, ulong userId)
    {
        var now = time.GetUtcNow();
        var adds = _adds.GetOrAdd((guildId, userId), _ => []);
        lock (adds)
        {
            adds.RemoveAll(at => now - at >= TimeSpan.FromMinutes(1));
            if (adds.Count >= AddsPerMemberPerMinute)
                return false;
            adds.Add(now);
            return true;
        }
    }

    // A control from someone watching; the answer.
    // A vote (or its taking back) on a queued video by someone in the channel; with reordering, the videos with
    // enough votes go first.
    public async Task<string> VoteAsync(Guild guild, GuildUser member, ulong channelId, int number)
    {
        if (!_rooms.TryGetValue((guild.Id, channelId), out var room))
            return "Nothing is being watched here.";
        var rules = await RulesAsync(guild.Id);
        await room.Gate.WaitAsync();
        try
        {
            if (room.Queue.ElementAtOrDefault(number - 1) is not { } item)
                return $"There's no #{number} up next.";
            if (!item.Votes.Remove(member.Id))
                item.Votes.Add(member.Id);
            if (rules.VideoVotes == QueueVoting.Reorder)
            {
                var ordered = QueueVotes.Reordered(room.Queue, i => i.Votes.Count, rules.VotesToMove);
                room.Queue.Clear();
                room.Queue.AddRange(ordered);
            }
            await points.InteractAsync(guild.Id, member.Id, InteractionKind.Vote, $"video {item.Video.Id}");
            return $"👍 **{item.Video.Title}**: {item.Votes.Count} {(item.Votes.Count == 1 ? "vote" : "votes")}{(rules.VideoVotes == QueueVoting.Reorder && item.Votes.Count >= rules.VotesToMove ? ", moved up" : "")}.";
        }
        finally
        {
            room.Gate.Release();
        }
    }

    // A queued video taken out by the one who added it, or by anyone with music.manage.
    public async Task<string> RemoveAsync(Guild guild, GuildUser member, ulong channelId, int number)
    {
        if (!_rooms.TryGetValue((guild.Id, channelId), out var room))
            return "Nothing is being watched here.";
        await room.Gate.WaitAsync();
        try
        {
            if (room.Queue.ElementAtOrDefault(number - 1) is not { } item)
                return $"There's no #{number} up next.";
            if (item.AddedBy != member.Id && !await access.CanAsync(guild, member, BotPermissions.ManageMusic))
                return $"Only the one who added it, or someone with `{BotPermissions.ManageMusic}`, takes a video out of the queue.";
            room.Queue.RemoveAt(number - 1);
            return $"🗑️ Took **{item.Video.Title}** out of the queue.";
        }
        finally
        {
            room.Gate.Release();
        }
    }

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

    // The voice channels watching something now, with what.
    public IReadOnlyList<(ulong ChannelId, string Title)> Playing(ulong guildId)
        => [.. _rooms.Values.Where(r => r.GuildId == guildId && r.Current is not null).Select(r => (r.ChannelId, r.Current!.Video.Title))];

    // What the Activity shows; null when nothing is being watched there.
    public JsonObject? State(ulong guildId, ulong channelId)
    {
        if (!_rooms.TryGetValue((guildId, channelId), out var room))
            return null;
        var now = time.GetUtcNow();
        var problem = room.Problem is { } p && now - p.At < ProblemShown ? p.Text : null;
        if (room.Current is not { } current)
            return problem is null ? null : new JsonObject { ["problem"] = problem };
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
            ["queue"] = new JsonArray([.. room.Queue.Take(QueueShown).Select(i => (JsonNode)new JsonObject { ["by"] = i.AddedBy.ToString(), ["votes"] = i.Votes.Count, ["voters"] = string.Join(',', i.Votes), ["title"] = i.Video.Title, ["seconds"] = i.Video.Seconds })]),
            ["problem"] = problem,
        };
    }

    private bool Start(WatchRoom room, WatchItem item)
    {
        if (streams.Start(room.GuildId, item.Video) is not { } id)
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
        streams.RemoveLeftovers();
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
                        if (room.Current is { StreamId: { } stream } current && streams.Failed(stream) is { } why)
                        {
                            logger.LogWarning("Streaming {Title} in {ChannelId} of {GuildId} failed: {Problem}", current.Video.Title, room.ChannelId, room.GuildId, why);
                            room.Problem = ($"“{current.Video.Title}” couldn't be streamed.", now);
                            Next(room);
                        }
                        if (room.Current?.Video.Seconds is { } length && !room.Paused && room.Position(now) >= length + 1)
                            Next(room);
                        var someone = presence.Snapshot(room.GuildId).Any(p => p.Value.ChannelId == room.ChannelId && !p.Value.IsBot);
                        room.EmptySince = someone ? null : room.EmptySince ?? now;
                        var showing = room.Problem is { } problem && now - problem.At < ProblemShown;
                        if (room.Current is null && room.Queue.Count == 0 && !showing || room.EmptySince is { } since && now - since > EmptyFor)
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
    public async Task AnnounceAsync(ulong guildId, ulong channelId, string text)
    {
        try
        {
            await rest.SendMessageAsync(channelId, new() { Content = text, AllowedMentions = AllowedMentionsProperties.None });
        }
        catch (RestException ex)
        {
            logger.LogDebug("Announcing in {ChannelId} of {GuildId}: {Message}", channelId, guildId, ex.Message);
        }
    }

    public override void Dispose()
    {
        streams.Dispose();
        base.Dispose();
    }
}
