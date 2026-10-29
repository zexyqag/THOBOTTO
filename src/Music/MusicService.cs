using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Helpers;
using THOBOTTO.Modules;
using THOBOTTO.Points;
using THOBOTTO.Voice.Speaking;

namespace THOBOTTO.Music;

// Music players on the helper bots: hands out a helper per voice channel, syncs more helpers into
// other channels to play along, lets helpers speak as the personality they wear, and sends them home
// when nobody listens or nothing plays for a while.
public sealed partial class MusicService(
    HelperFleet fleet,
    IOptions<LavalinkOptions> lavalink,
    RestClient rest,
    GatewayClient gateway,
    VoicePresence presence,
    SettingsStore settings,
    PersonalityBook personalities,
    AccessControl access,
    IDbContextFactory<BotDbContext> dbFactory,
    ListeningSeats listening,
    ListenTracker listens,
    Scrobbler scrobbler,
    PlayHistory history,
    HelperSpeech speech,
    IHttpClientFactory http,
    PointsEngine points,
    TimeProvider time,
    ILoggerFactory loggers,
    SpeechClips clips) : BackgroundService, IHelperAware, IHelperWork, IClipPlayer
{
    public const string ModuleId = "music";

    private readonly DailyAllowance _freeAdds = new();

    private static readonly TimeSpan IdleCheck = TimeSpan.FromSeconds(30);
    private static readonly string[] YoutubeSearches = ["ytmsearch", "ytsearch"];
    private const long AutoplayLongest = 15 * 60_000;

    private readonly ILogger _logger = loggers.CreateLogger<MusicService>();
    private readonly HttpClient _lavalinkHttp = http.CreateClient(LavalinkClient.HttpClientName);
    private readonly SemaphoreSlim _assign = new(1, 1);
    // (helper, guild) → the player that helper runs there: its own, or one it plays along with.
    private readonly ConcurrentDictionary<(ulong Helper, ulong Guild), MusicPlayer> _players = new();
    private bool _savedAny;
    public IReadOnlyList<HelperBot> Helpers => fleet.Helpers;

    // What Lavalink finds for a link, a search or a playlist.
    public Task<LoadResult> LoadAsync(string identifier, ulong requestedBy) => LavalinkClient.LoadAsync(_lavalinkHttp, lavalink.Value, identifier, requestedBy);

    // Every player (a synced one once, though several helpers play it).
    public IReadOnlyList<MusicPlayer> Players => _players.Values.Distinct().ToList();

    public IReadOnlyList<MusicPlayer> PlayersIn(ulong guildId)
        => _players.Where(p => p.Key.Guild == guildId).Select(p => p.Value).Distinct().ToList();

    public MusicPlayer? PlayerIn(ulong guildId, ulong voiceChannelId) => PlayersIn(guildId).FirstOrDefault(p => p.Plays(voiceChannelId));

    // The player a helper runs in a guild: its own, or one it plays along with.
    public MusicPlayer? PlayerOf(ulong helperId, ulong guildId) => _players.GetValueOrDefault((helperId, guildId));

    public HelperBot? PlayingIn(ulong guildId, ulong voiceChannelId) => PlayerIn(guildId, voiceChannelId)?.Helper;

    // A helper's words where it plays without the relay: a clip Lavalink fetches from the bot, in the music's place.
    public async Task<bool> PlayClipAsync(ulong guildId, ulong voiceChannelId, string url, TimeSpan length)
    {
        if (PlayerIn(guildId, voiceChannelId) is not { } player || player.Quizzing)
        {
            _logger.LogWarning("A spoken clip for {GuildId} has no player to play it: {Why}", guildId, PlayerIn(guildId, voiceChannelId) is null ? "none in the channel" : "a quiz is on");
            return false;
        }
        var loaded = await LoadAsync(url, player.Helper.UserId);
        if (loaded.Tracks.FirstOrDefault() is not { } clip)
        {
            _logger.LogWarning("Lavalink couldn't load a spoken clip for {GuildId}: {Error}", guildId, loaded.Error);
            return false;
        }
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await player.InterludeAsync(clip, ended);
        _logger.LogInformation("Spoken in the music's place in {GuildId}: {Seconds:0.0} s", guildId, length.TotalSeconds);
        try
        {
            await ended.Task.WaitAsync(length + TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            await player.ResumeAsync();
        }
        return true;
    }

    public bool Plays(ulong helperId, ulong guildId) => _players.ContainsKey((helperId, guildId));

    // The players a helper runs, one per guild.
    public IReadOnlyList<MusicPlayer> PlayersOf(ulong helperId)
        => _players.Where(p => p.Key.Helper == helperId).Select(p => p.Value).ToList();

    // Loads what was asked for and plays or queues it in the voice channel, bringing a helper if none plays there.
    // Returns the reply: what plays or got queued, or why not.
    public async Task<string> PlayAsync(ulong guildId, ulong userId, ulong voiceChannelId, ulong textChannelId, string query, Placement placement, ulong? helper = null, SearchOn? on = null)
    {
        var rules = await settings.GetAsync<MusicRules>(guildId, ModuleId);
        var text = query.Trim();
        var search = on is { } picked ? MusicQueries.Prefix(picked) : rules.DefaultSearch;
        var identifier = MusicQueries.Identifier(text, search);
        var asTyped = identifier != $"{search}:{text}";

        LoadResult loaded;
        try
        {
            loaded = await LoadAsync(identifier, userId);
            // Plain words the chosen search can't find (or a source that's failing): YouTube Music, then YouTube.
            foreach (var other in YoutubeSearches.Where(o => !asTyped && o != search))
            {
                if (loaded.Error is null && loaded.Tracks.Count > 0)
                    break;
                loaded = await LoadAsync($"{other}:{text}", userId);
            }
        }
        catch (HttpRequestException)
        {
            return "The music server isn't reachable right now.";
        }

        _logger.LogDebug("Looked up {Identifier} in {GuildId}: {Count} track(s){Error}", identifier, guildId, loaded.Tracks.Count, loaded.Error is null ? "" : $", {loaded.Error}");
        if (loaded.Error is not null || loaded.Tracks.Count == 0)
            return loaded.Error is null ? "Nothing found." : $"Couldn't load that: {loaded.Error}";

        // A search plays its best match; a playlist goes in whole.
        return await QueueAsync(guildId, userId, voiceChannelId, textChannelId, loaded.Playlist is null ? loaded.Tracks.Take(1).ToList() : loaded.Tracks, loaded.Playlist, placement, helper);
    }

    // Plays or queues tracks in the voice channel, up to the queue limit, bringing a helper if none plays there.
    // A playlist goes with the playlists, after the songs people added (playing now, it replaces them).
    public async Task<string> QueueAsync(ulong guildId, ulong userId, ulong voiceChannelId, ulong textChannelId, IReadOnlyList<Track> tracks, string? playlist, Placement placement, ulong? helper = null)
    {
        var rules = await settings.GetAsync<MusicRules>(guildId, ModuleId);
        var (player, problem) = await PlayerForAsync(guildId, voiceChannelId, textChannelId, helper, insist: helper is not null);
        if (player is null)
            return problem!;

        var room = Math.Max(0, rules.MaxQueue - player.Queue.Count);
        var fitting = tracks.Take(room).ToList();
        if (fitting.Count == 0)
            return $"The queue is full ({rules.MaxQueue}).";

        var what = playlist is not null ? $"**{fitting.Count}** tracks from **{playlist}**" : fitting[0].Markdown;
        var price = rules.AddCost > 0 && await points.ChargesAsync(guildId) && !AddPrice.Free(Listeners(player).Count, rules.FreeWithListeners)
            && !_freeAdds.Take(guildId, userId, time.GetUtcNow(), rules.FreeAddsPerDay)
            ? AddPrice.Of(rules.AddCost, rules.CostPerWaiting, player.AddedBy(userId), playlist is not null ? rules.PlaylistCostsLike : 1) : 0;
        if (price > 0 && !await points.TrySpendAsync(guildId, userId, price, $"added {playlist ?? fitting[0].Title}"))
            return $"Adding more today costs {points.Rules(guildId).Format(price)}, and you don't have that.";
        var allowed = await PlacementAsync(guildId, userId, player, placement, rules);
        var position = await player.AddAsync(fitting, playlist is not null, allowed);
        var why = allowed == placement ? "" : $" Only who may skip here puts songs {(placement == Placement.Now ? "on now" : "first")}.";
        var paid = price > 0 ? $" · {points.Rules(guildId).Format(price)}" : "";
        return position == 0 ? $"▶️ {what}{paid}" : $"➕ Queued {what} (#{position}).{why}{paid}";
    }

    // Jumping the queue skips or delays others' songs: with only DJs in control, that's for DJs and whoever asked
    // for what plays. Others' songs go first (without skipping), or last when the server says so.
    private async Task<Placement> PlacementAsync(ulong guildId, ulong userId, MusicPlayer player, Placement wanted, MusicRules rules)
    {
        if (wanted == Placement.Last || !rules.DjOnly || player.Current is not { } current || current.RequestedBy == userId)
            return wanted;
        if (gateway.Cache.Guilds.TryGetValue(guildId, out var guild) && guild.Users.TryGetValue(userId, out var user)
            && await access.CanAsync(guild, user, BotPermissions.MusicDj))
            return wanted;
        return rules.OthersQueueLast ? Placement.Last : Placement.First;
    }

    // The player in that voice channel, or a free helper sent there (the preferred one when it's free).
    // Null with a reason when none is free. Quiet: no "joined" line, as when picking up after a restart.
    // The player in that channel, or a new one with a helper sent there: the one preferred when it can (insisted
    // on, it's that one or nothing, and one playing there already hands over to it).
    public async Task<(MusicPlayer? Player, string? Problem)> PlayerForAsync(ulong guildId, ulong voiceChannelId, ulong textChannelId, ulong? preferredHelper = null, bool quiet = false, bool insist = false)
    {
        await _assign.WaitAsync();
        try
        {
            if (PlayerIn(guildId, voiceChannelId) is { } existing)
            {
                existing.TextChannelId = textChannelId;
                if (!insist || preferredHelper is not { } wanted || existing.Helper.UserId == wanted)
                    return (existing, null);
                return await SwapCoreAsync(existing, wanted);
            }
            return await StartPlayerAsync(guildId, voiceChannelId, textChannelId, preferredHelper, quiet, insist);
        }
        finally
        {
            _assign.Release();
        }
    }

    private async Task<(MusicPlayer? Player, string? Problem)> StartPlayerAsync(ulong guildId, ulong voiceChannelId, ulong textChannelId, ulong? preferredHelper, bool quiet, bool insist)
    {
        var (free, problem) = await SendHelperAsync(guildId, voiceChannelId, preferredHelper, insist);
        if (free is null)
            return (null, problem);

        var rules = await settings.GetAsync<MusicRules>(guildId, ModuleId);
        var player = new MusicPlayer(free, guildId, voiceChannelId, textChannelId, time, rules.HelpersVolume) { Autoplay = rules.Autoplay };
        player.Changed += OnChangedAsync;
        player.RanOut += OnRanOutAsync;
        _players[(free.UserId, guildId)] = player;
        if (!quiet)
            await SpeakAsync(guildId, free, textChannelId, Moments.Joined, Values(voiceChannelId: voiceChannelId), voiceChannelId);
        return (player, null);
    }

    // The music in the player's channel handed to another helper, where it is: the queue and position go with it.
    public async Task<string> SwapAsync(MusicPlayer player, ulong helperId)
    {
        await _assign.WaitAsync();
        try
        {
            if (player.Helper.UserId == helperId)
                return $"{await personalities.NameAsync(player.GuildId, player.Helper)} is the one playing here.";
            var (fresh, problem) = await SwapCoreAsync(player, helperId);
            return fresh is null ? problem! : $"🎵 {await personalities.NameAsync(player.GuildId, fresh.Helper)} has taken over here.";
        }
        finally
        {
            _assign.Release();
        }
    }

    private async Task<(MusicPlayer? Player, string? Problem)> SwapCoreAsync(MusicPlayer player, ulong helperId)
    {
        if (Helpers.FirstOrDefault(h => h.UserId == helperId && h.InGuild(player.GuildId)) is not { } target)
            return (null, "That helper isn't in this server.");
        if (Plays(helperId, player.GuildId))
            return (null, $"{await personalities.NameAsync(player.GuildId, target)} is playing in another channel here.");
        var state = await player.SaveAsync();
        await DisconnectAsync(player, stayToListen: false);
        var (fresh, problem) = await StartPlayerAsync(player.GuildId, player.VoiceChannelId, player.TextChannelId, helperId, quiet: false, insist: true);
        if (fresh is null)
            return (null, problem);
        await fresh.RestoreAsync(state with { HelperId = helperId });
        foreach (var mirror in state.Mirrors)
            await SyncAsync(fresh, mirror.VoiceChannelId, mirror.HelperId, quiet: true);
        return (fresh, null);
    }

    // Sends another helper to play along in that channel. Null, or why not.
    public async Task<string?> SyncAsync(MusicPlayer player, ulong voiceChannelId, ulong? preferredHelper = null, bool quiet = false)
    {
        await _assign.WaitAsync();
        try
        {
            if (PlayerIn(player.GuildId, voiceChannelId) is { } there)
                return there == player ? "That channel already plays along." : "Other music plays there already.";

            var (free, problem) = await SendHelperAsync(player.GuildId, voiceChannelId, preferredHelper);
            if (free is null)
                return problem;

            _players[(free.UserId, player.GuildId)] = player;
            await player.AddMirrorAsync(new(free, voiceChannelId));
            if (!quiet)
                await SpeakAsync(player.GuildId, free, player.TextChannelId, Moments.Joined, Values(voiceChannelId: voiceChannelId));
            return null;
        }
        finally
        {
            _assign.Release();
        }
    }

    public async Task UnsyncAsync(MusicPlayer player, Mirror mirror)
    {
        await player.RemoveMirrorAsync(mirror);
        _players.TryRemove((mirror.Helper.UserId, player.GuildId), out _);
        await mirror.Helper.LeaveAsync(player.GuildId);
    }

    // Through the relay, a helper whose channel still wants a listener stays there to listen, unless it's needed
    // elsewhere (stayToListen false).
    public async Task DisconnectAsync(MusicPlayer player, bool stayToListen = true)
    {
        Heard(player, null);
        foreach (var mirror in player.Mirrors)
            await UnsyncAsync(player, mirror);
        _players.TryRemove((player.Helper.UserId, player.GuildId), out _);
        await DeleteNowPlayingAsync(player);
        if (stayToListen && listening.TakeBack is { } takeBack && await takeBack(player.Helper, player.GuildId, player.VoiceChannelId))
            return;
        await player.Helper.LeaveAsync(player.GuildId);
    }

    bool IHelperWork.Busy(ulong helperId, ulong guildId) => Plays(helperId, guildId);

    // When listening comes first in the server, a player elsewhere in it stops and its helper goes to listen.
    async Task<HelperBot?> IHelperWork.YieldAsync(ulong guildId, ulong channelId)
    {
        if ((await settings.GetAsync<MusicRules>(guildId, ModuleId)).Priority != HelperPriorities.ListeningFirst)
            return null;
        if (PlayersIn(guildId).FirstOrDefault(p => !p.Plays(channelId)) is not { } player)
            return null;
        await player.StopAsync();
        await DisconnectAsync(player, stayToListen: false);
        return player.Helper;
    }

    // Lowers the music in a channel where people talk (and brings it back), where the server wants that; the helper
    // playing in that channel only, a step a tick so it fades rather than jumps.
    async Task IHelperWork.TalkingAsync(ulong guildId, ulong channelId, bool talking)
    {
        if (PlayerIn(guildId, channelId) is not { } player)
            return;
        var rules = await settings.GetAsync<MusicRules>(guildId, ModuleId);
        var playing = player.Mirrors.FirstOrDefault(m => m.VoiceChannelId == channelId)?.Helper ?? player.Helper;
        var target = talking && rules.DuckWhileTalking ? rules.DuckPercent : 100;
        if (player.LevelOf(playing) == target)
            return;
        try
        {
            await player.FadeAsync(playing, target);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            _logger.LogDebug("Lowering the music in {GuildId}: {Message}", guildId, ex.Message);
        }
    }

    // A vote from someone listening; the server's share of the listeners (at least one) skips the track.
    public async Task<string> VoteSkipAsync(MusicPlayer player, ulong userId)
    {
        if (!Listeners(player).Contains(userId))
            return "Only someone listening can vote to skip.";
        if (player.Current is not { } current)
            return "Nothing is playing.";

        await player.VoteSkipAsync(userId);
        var (votes, needed) = await SkipTallyAsync(player);
        if (votes < needed)
            return $"🗳️ Vote to skip **{current.Title}**: {votes} of {needed}.";
        await player.SkipAsync();
        return await LineAsync(player.GuildId, player.Helper, Moments.Skipped);
    }

    // Votes to skip what plays, from those listening, and how many it takes.
    public async Task<(int Votes, int Needed)> SkipTallyAsync(MusicPlayer player)
    {
        var listeners = Listeners(player);
        var percent = (await settings.GetAsync<MusicRules>(player.GuildId, ModuleId)).SkipVotePercent;
        return ((await player.SkipVotesAsync()).Count(listeners.Contains), Math.Max(1, (int)Math.Ceiling(listeners.Count * percent / 100.0)));
    }

    private HashSet<ulong> Listeners(MusicPlayer player)
        => presence.Snapshot(player.GuildId).Where(p => !p.Value.IsBot && player.Plays(p.Value.ChannelId)).Select(p => p.Key).ToHashSet();

    // Why the user may not control that player, or null when they may.
    public async Task<string?> RefusalAsync(ulong guildId, GuildUser user, MusicPlayer player, bool ownTrackAllowed = false)
    {
        var rules = await settings.GetAsync<MusicRules>(guildId, ModuleId);
        var own = ownTrackAllowed && player.Current?.RequestedBy == user.Id;
        return rules.DjOnly && !own && gateway.Cache.Guilds.TryGetValue(guildId, out var guild) && !await access.CanAsync(guild, user, BotPermissions.MusicDj)
            ? $"That needs `{BotPermissions.MusicDj}` here."
            : null;
    }

    // A vote on a queued song by someone in the channel; the count after, as the reply.
    public async Task<string> VoteAsync(ulong guildId, ulong userId, MusicPlayer player, int number)
    {
        var rules = await settings.GetAsync<MusicRules>(guildId, ModuleId);
        if (player.Queue.ElementAtOrDefault(number - 1) is not { } track || number > player.AddedCount)
            return $"There's no #{number} among the songs people added.";
        if (await player.VoteAsync(number, userId, rules.SongVotes == QueueVoting.Reorder, rules.VotesToMove) is not { } votes)
            return $"There's no #{number} among the songs people added.";
        await points.InteractAsync(guildId, userId, InteractionKind.Vote, $"song {track.Encoded}");
        return $"👍 {track.Markdown}: {votes} {(votes == 1 ? "vote" : "votes")}{(rules.SongVotes == QueueVoting.Reorder && votes >= rules.VotesToMove ? ", moved up" : "")}.";
    }

    // A queued track taken out by the one who asked for it, or by anyone with music.manage.
    public async Task<string> RemoveAsync(ulong guildId, GuildUser user, MusicPlayer player, int number)
    {
        if (player.Queue.ElementAtOrDefault(number - 1) is not { } track)
            return $"There's no #{number} in the queue.";
        if (track.RequestedBy != user.Id && !(gateway.Cache.Guilds.TryGetValue(guildId, out var guild) && await access.CanAsync(guild, user, BotPermissions.ManageMusic)))
            return $"Only the one who asked for it, or someone with `{BotPermissions.ManageMusic}`, takes a song out of the queue.";
        return await player.RemoveAsync(number) is { } removed ? $"🗑️ Took {removed.Markdown} out of the queue." : $"There's no #{number} in the queue.";
    }

    // A helper's line for a moment, prefixed with its name for posts the main bot makes on its behalf.
    public async Task<string> LineAsync(ulong guildId, HelperBot helper, string moment, Track? track = null)
        => $"**{await personalities.NameAsync(guildId, helper)}:** {await personalities.SayAsync(guildId, helper, moment, Values(track))}";

    // The now-playing buttons, wherever they were pressed (helpers post their own messages).
    public async Task<string> ButtonAsync(ulong guildId, GuildUser user, string action, ulong helperId)
    {
        if (Helpers.FirstOrDefault(h => h.UserId == helperId) is not { } helper || PlayerOf(helperId, guildId) is not { } player)
            return "That player has stopped.";

        if (await RefusalAsync(guildId, user, player, ownTrackAllowed: action == "skip") is { } refusal)
            return action == "skip" ? await VoteSkipAsync(player, user.Id) : refusal;

        switch (action)
        {
            case "pause":
                await player.SetPausedAsync(!player.Paused);
                return player.Paused ? "⏸️ Paused." : "▶️ Resumed.";
            case "skip":
                await player.SkipAsync();
                return await LineAsync(guildId, helper, Moments.Skipped);
            default:
                await player.StopAsync();
                await DisconnectAsync(player);
                return await LineAsync(guildId, helper, Moments.Stopped);
        }
    }

    public Task AttachAsync(HelperBot helper)
    {
        helper.Lavalink.Event += e => OnLavalinkEventAsync(helper, e);
        helper.Disconnected += guildId => OnThrownOutAsync(helper, guildId);
        helper.Reconnected += () => OnReconnectedAsync(helper);
        helper.Lavalink.PlayerUpdate += u => OnPositionAsync(helper, u);
        helper.Gateway.InteractionCreate += interaction => OnHelperInteractionAsync(helper, interaction);
        return Task.CompletedTask;
    }

    // A helper being removed stops what it plays, or stops playing along; its players are forgotten even when
    // stopping them fails (Lavalink gone), so nothing keeps handing out a player of a helper that is no more.
    public async Task DetachAsync(HelperBot helper)
    {
        try
        {
            foreach (var player in PlayersOf(helper.UserId))
                await OnThrownOutAsync(helper, player.GuildId);
        }
        finally
        {
            foreach (var key in _players.Keys.Where(k => k.Helper == helper.UserId))
                _players.TryRemove(key, out _);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        clips.Player = this;
        settings.Changed += OnSettingsChangedAsync;
        // Saving waits for this, so what's saved isn't overwritten before it's back.
        await RestoreAllAsync(stoppingToken);

        // A server that wants its helpers gone the moment nobody is left doesn't wait for the next look.
        presence.Changed += (state, before) => before is null ? Task.CompletedTask : LeftAloneAsync(state.GuildId);
        using var timer = new PeriodicTimer(IdleCheck, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await LeaveIdleAsync();
                await SaveAllAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Checking idle music players failed");
            }
        }
    }

    // Stops before the helpers do (registered after them), so they're still playing to be saved.
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            await SaveAllAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving the music players failed");
        }
    }

    // Saved every idle check and on shutdown; a crash loses at most the last half minute.
    private async Task SaveAllAsync()
    {
        var rows = new List<SavedMusicPlayer>();
        foreach (var player in Players)
        {
            var state = await player.SaveAsync();
            if (state.Current is not null || state.Queue.Count > 0)
                rows.Add(new() { GuildId = player.GuildId, VoiceChannelId = player.VoiceChannelId, State = JsonSerializer.Serialize(state), SavedAt = time.GetUtcNow() });
        }
        if (rows.Count == 0 && !_savedAny)
            return;

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.SavedMusicPlayers.ExecuteDeleteAsync();
        db.SavedMusicPlayers.AddRange(rows);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        _savedAny = rows.Count > 0;
    }

    // After a restart: music comes back where someone is still listening, with the helper it had when that one's free.
    private async Task RestoreAllAsync(CancellationToken ct)
    {
        List<SavedMusicPlayer> saved;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            saved = await db.SavedMusicPlayers.AsNoTracking().ToListAsync(ct);
        if (saved.Count == 0)
            return;
        _savedAny = true;

        var states = saved.Select(s => (Row: s, State: JsonSerializer.Deserialize<PlayerState>(s.State)!)).ToList();
        // The helpers connect to Discord and Lavalink as the bot starts; give them a minute.
        var until = time.GetUtcNow().AddMinutes(1);
        while (time.GetUtcNow() < until && !states.All(s =>
            gateway.Cache.Guilds.ContainsKey(s.Row.GuildId)
            && Helpers.Any(h => h.UserId == s.State.HelperId && h.InGuild(s.Row.GuildId) && h.Lavalink.SessionId is not null)))
            await Task.Delay(TimeSpan.FromSeconds(1), time, ct);

        foreach (var (row, state) in states)
        {
            try
            {
                await RestoreAsync(row.GuildId, state);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Picking the music in {ChannelId} of {GuildId} up again failed", state.VoiceChannelId, row.GuildId);
            }
        }
    }

    // The server's helpers' volume reaches every helper playing there the moment the setting changes.
    private async Task OnSettingsChangedAsync(ulong guildId, string module)
    {
        if (module != ModuleId)
            return;
        var volume = (await settings.GetAsync<MusicRules>(guildId, ModuleId)).HelpersVolume;
        foreach (var player in Players.Where(p => p.GuildId == guildId && p.Volume != volume))
            await player.SetVolumeAsync(volume);
    }

    private async Task RestoreAsync(ulong guildId, PlayerState state)
    {
        var listening = presence.Snapshot(guildId).Values.Where(p => !p.IsBot).Select(p => p.ChannelId).ToHashSet();
        if (!listening.Contains(state.VoiceChannelId) && !state.Mirrors.Any(m => listening.Contains(m.VoiceChannelId)))
            return;

        var (player, problem) = await PlayerForAsync(guildId, state.VoiceChannelId, state.TextChannelId, state.HelperId, quiet: true);
        if (player is null)
        {
            _logger.LogInformation("Music in {ChannelId} of {GuildId} didn't come back: {Problem}", state.VoiceChannelId, guildId, problem);
            return;
        }
        await player.RestoreAsync(state);
        foreach (var mirror in state.Mirrors.Where(m => listening.Contains(m.VoiceChannelId)))
            await SyncAsync(player, mirror.VoiceChannelId, mirror.HelperId, quiet: true);
        _logger.LogInformation("Music in {ChannelId} of {GuildId} picked up again", state.VoiceChannelId, guildId);
    }

    private async Task LeftAloneAsync(ulong guildId)
    {
        if (!Players.Any(p => p.GuildId == guildId) || (await settings.GetAsync<MusicRules>(guildId, ModuleId)).LonelyMinutes > 0)
            return;
        try
        {
            await LeaveIdleAsync(guildId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Checking the music players of {GuildId} after someone left failed", guildId);
        }
    }

    private async Task LeaveIdleAsync(ulong? onlyGuildId = null)
    {
        var now = time.GetUtcNow();
        foreach (var player in Players.Where(p => onlyGuildId is null || p.GuildId == onlyGuildId))
        {
            var rules = await settings.GetAsync<MusicRules>(player.GuildId, ModuleId);
            var lonelyLimit = TimeSpan.FromMinutes(rules.LonelyMinutes);
            var present = presence.Snapshot(player.GuildId).Values.Where(p => !p.IsBot).ToList();

            // A channel playing along with nobody in it goes on its own; the queue stays while anyone listens anywhere.
            foreach (var mirror in player.Mirrors)
            {
                if (present.Any(p => p.ChannelId == mirror.VoiceChannelId))
                    mirror.LonelySince = null;
                else if ((mirror.LonelySince ??= now) <= now - lonelyLimit)
                {
                    await UnsyncAsync(player, mirror);
                    await SpeakAsync(player.GuildId, mirror.Helper, player.TextChannelId, Moments.Lonely, Values());
                }
            }

            var listeners = present.Count(p => player.Plays(p.ChannelId));
            player.LonelySince = listeners == 0 ? player.LonelySince ?? now : null;
            if (MusicIdle.LeaveReason(rules, player.Current is not null || player.Quizzing, now - player.IdleSince, listeners, player.LonelySince is { } since ? now - since : null) is { } reason)
            {
                // Its goodbye is said before it goes, when anyone's there to hear it.
                var line = await personalities.SayAsync(player.GuildId, player.Helper, reason, Values());
                if (listeners > 0)
                    await speech.SayAsync(player.GuildId, player.VoiceChannelId, line.Said, SpeechKind.JoinAndLeave);
                await DisconnectAsync(player);
                await personalities.PostAsync(player.GuildId, player.Helper, player.TextChannelId, new() { Content = line, AllowedMentions = AllowedMentionsProperties.None });
            }
        }
    }

    private async ValueTask OnHelperInteractionAsync(HelperBot helper, Interaction interaction)
    {
        if (interaction is not ButtonInteraction { Data.CustomId: var id } button || !id.StartsWith("music:")
            || button.GuildId is not { } guildId || button.User is not GuildUser user)
            return;

        var parts = id.Split(':');
        var text = await ButtonAsync(guildId, user, parts[1], ulong.Parse(parts[2]));
        await button.SendResponseAsync(InteractionCallback.Message(new() { Content = text, Flags = MessageFlags.Ephemeral, AllowedMentions = AllowedMentionsProperties.None }));
    }

    private async Task OnLavalinkEventAsync(HelperBot helper, LavalinkEvent e)
    {
        if (PlayerOf(helper.UserId, e.GuildId) is not { } player)
            return;
        if (player.Helper != helper)
        {
            // A mirror follows the leader's track ends; it only goes when it's thrown out.
            if (e is { Type: "WebSocketClosedEvent", Reason: "4014" or "4006" } && player.Mirrors.FirstOrDefault(m => m.Helper == helper) is { } mirror)
                await UnsyncAsync(player, mirror);
            return;
        }

        switch (e.Type)
        {
            case "TrackEndEvent":
                if (e.Reason == "loadFailed")
                    listens.Failed(player);
                await player.TrackEndedAsync(e.Reason);
                break;
            case "TrackExceptionEvent":
                listens.Failed(player);
                // Lavalink's message carries a stack trace after the first line.
                var reason = e.Message?.Split('\n')[0].Trim() ?? "unknown error";
                await personalities.PostAsync(player.GuildId, helper, player.TextChannelId, new()
                {
                    Content = $"Couldn't play **{player.Current?.Title ?? "that"}**: {(reason.Length > 200 ? reason[..200] + "…" : reason)}",
                    AllowedMentions = AllowedMentionsProperties.None,
                });
                break;
            case "TrackStuckEvent":
                await player.SkipAsync();
                break;
            case "WebSocketClosedEvent" when e.Reason is "4014" or "4006":
                // Disconnected or the session went away (kicked, channel deleted).
                await DisconnectAsync(player);
                break;
        }
    }

    // Someone disconnected the helper: its channel stops playing (along), the rest carries on.
    private async Task OnThrownOutAsync(HelperBot helper, ulong guildId)
    {
        if (PlayerOf(helper.UserId, guildId) is not { } player)
            return;
        if (player.Helper != helper)
        {
            if (player.Mirrors.FirstOrDefault(m => m.Helper == helper) is { } mirror)
                await UnsyncAsync(player, mirror);
            return;
        }
        await player.StopAsync();
        await DisconnectAsync(player);
    }

    // Its Lavalink came back with a fresh session: whatever it played goes on from where it would be by now.
    private async Task OnReconnectedAsync(HelperBot helper)
    {
        foreach (var player in PlayersOf(helper.UserId))
            await player.ResumeAsync(helper);
    }

    // Scrobbles and writes down what was heard; both do their work in the background.
    private void Heard(MusicPlayer player, Track? next)
    {
        var (ended, started) = listens.Changed(player, next);
        scrobbler.Record(ended, started);
        history.Record(ended);
    }

    // Called inside the player's lock, so the songs are found and queued after it's released.
    private Task OnRanOutAsync(MusicPlayer player, Track last)
    {
        if (player.Autoplay)
            _ = Task.Run(() => AutoplayAsync(player, last));
        return Task.CompletedTask;
    }

    // Queues a few songs from YouTube Music's mix for the last one (found on YouTube Music when it came from elsewhere).
    private async Task AutoplayAsync(MusicPlayer player, Track last)
    {
        try
        {
            var seed = last.Source == "youtube" ? last.Identifier
                : (await LoadAsync($"ytmsearch:{last.Title} {last.Author.Split(',')[0]}", player.Helper.UserId)).Tracks.FirstOrDefault()?.Identifier;
            if (seed is null)
                return;
            var mix = await LoadAsync($"https://music.youtube.com/watch?v={seed}&list=RDAMVM{seed}", player.Helper.UserId);
            var picks = mix.Tracks
                .Where(t => t.Identifier != seed && !t.IsStream && t.LengthMs <= AutoplayLongest && !player.HasPlayed(t))
                .DistinctBy(t => t.Title.ToLowerInvariant())
                .Take((await settings.GetAsync<MusicRules>(player.GuildId, ModuleId)).AutoplayBatch)
                .ToList();
            // Someone may have queued something or stopped the music meanwhile.
            if (picks.Count == 0 || player.Current is not null || player.Queue.Count > 0 || PlayerOf(player.Helper.UserId, player.GuildId) != player)
                return;
            await player.AddAsync(picks, playlist: true, Placement.Last);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            _logger.LogWarning("Autoplay after {Title} in {GuildId} failed: {Message}", last.Title, player.GuildId, ex.Message);
        }
    }

    private async Task OnPositionAsync(HelperBot helper, LavalinkPosition update)
    {
        if (PlayerOf(helper.UserId, update.GuildId) is { } player && await player.PositionAsync(helper, update.Time, update.Position) is { } drift)
            _logger.LogDebug("{Helper} was {Drift} ms off in {GuildId}; caught up", helper.Name, drift, update.GuildId);
    }

    private async Task OnChangedAsync(MusicPlayer player, Track? track)
    {
        Heard(player, track);
        await DeleteNowPlayingAsync(player);
        if (track is null)
            return;

        var helper = player.Helper;
        var line = await personalities.SayAsync(player.GuildId, helper, Moments.Playing, Values(track));
        _ = speech.SayAsync(player.GuildId, player.VoiceChannelId, line.Said, SpeechKind.NowPlaying);
        var message = new MessageProperties
        {
            Embeds = [new()
            {
                Description = $"{line}\n{track.Author} · {track.Length}"
                    + (player.Queue.Count > 0 ? $"\nUp next: {player.Queue[0].Title}{(player.Queue.Count > 1 ? $" (+{player.Queue.Count - 1} more)" : "")}" : "")
                    + $"\n-# In {string.Join(", ", player.Channels.Select(c => $"<#{c}>"))}{(player.Loop != LoopMode.Off ? $" · loop: {player.Loop.ToString().ToLowerInvariant()}" : "")}",
                Color = new(await personalities.ColorAsync(player.GuildId, helper)),
            }],
            Components = [new ActionRowProperties
            {
                new ButtonProperties($"music:pause:{helper.UserId}", EmojiProperties.Standard("⏯️"), ButtonStyle.Secondary),
                new ButtonProperties($"music:skip:{helper.UserId}", EmojiProperties.Standard("⏭️"), ButtonStyle.Secondary),
                new ButtonProperties($"music:stop:{helper.UserId}", EmojiProperties.Standard("⏹️"), ButtonStyle.Secondary),
            }],
            AllowedMentions = AllowedMentionsProperties.None,
        };

        if (await personalities.PostAsync(player.GuildId, helper, player.TextChannelId, message) is { } posted)
            (player.NowPlayingMessageId, player.NowPlayingByHelper) = posted;
    }

    // A free helper, connected to that channel; else null and why. Among the equally fit, one at random; the one
    // preferred first, and insisted on, that one or nothing.
    private async Task<(HelperBot? Helper, string? Problem)> SendHelperAsync(ulong guildId, ulong voiceChannelId, ulong? preferred = null, bool insist = false)
    {
        if (insist && preferred is { } wanted)
        {
            if (Helpers.FirstOrDefault(h => h.UserId == wanted && h.InGuild(guildId)) is not { } named)
                return (null, "That helper isn't in this server.");
            if (Plays(wanted, guildId))
                return (null, $"{await personalities.NameAsync(guildId, named)} is playing in another channel here.");
        }
        // Through the relay, the helper listening in that channel plays there too, on the connection it has (it
        // goes on hearing, so the priority doesn't matter).
        if (listening.ListenerIn(guildId, voiceChannelId) is { } listenerId && (preferred ?? listenerId) == listenerId
            && Helpers.FirstOrDefault(h => h.UserId == listenerId && h.RelayOn && !Plays(h.UserId, guildId) && h.Lavalink.SessionId is not null) is { } listener
            && listening.HandOver?.Invoke(guildId, listenerId) is { } client)
        {
            if (await listener.PlayThroughAsync(guildId, voiceChannelId, client))
                return (listener, null);
            await listener.LeaveAsync(guildId);
            return (null, "The helper couldn't start playing there.");
        }

        // A helper listening for voice commands is picked last, and only when music comes first in this server
        // (it stops listening to play).
        var musicFirst = (await settings.GetAsync<MusicRules>(guildId, ModuleId)).Priority == HelperPriorities.MusicFirst;
        var helpers = Helpers.Where(h => h.InGuild(guildId) && (insist ? h.UserId == preferred : musicFirst || !listening.IsListening(guildId, h.UserId)))
            .OrderBy(h => h.UserId == preferred ? 0 : listening.IsListening(guildId, h.UserId) ? 2 : 1).ThenBy(_ => Random.Shared.Next()).ToList();
        if (helpers.Count == 0)
            return (null, "No helper bot is in this server yet. `/music helpers` has invite links.");
        if (helpers.FirstOrDefault(h => !Plays(h.UserId, guildId) && h.Lavalink.SessionId is not null) is not { } free)
            return (null, helpers.Any(h => h.Lavalink.SessionId is not null) ? "Every helper bot is busy in another channel." : "The music server isn't reachable right now.");

        await listening.FreeAsync(guildId, free.UserId);
        if (await free.JoinAsync(guildId, voiceChannelId))
            return (free, null);
        await free.LeaveAsync(guildId);
        return (null, "The helper couldn't connect to that channel. Can it see it and connect there?");
    }

    // Says a moment's line as the helper: written, and out loud in the voice channel when given.
    private async Task SpeakAsync(ulong guildId, HelperBot helper, ulong channelId, string moment, IReadOnlyDictionary<string, string> values, ulong? voiceChannelId = null)
    {
        var line = await personalities.SayAsync(guildId, helper, moment, values);
        await personalities.PostAsync(guildId, helper, channelId, new() { Content = line, AllowedMentions = AllowedMentionsProperties.None });
        if (voiceChannelId is { } voice)
            _ = speech.SayAsync(guildId, voice, line.Said, SpeechKind.JoinAndLeave);
    }

    private async Task DeleteNowPlayingAsync(MusicPlayer player)
    {
        if (player.NowPlayingMessageId is not { } id)
            return;
        player.NowPlayingMessageId = null;
        try
        {
            await (player.NowPlayingByHelper ? player.Helper.Gateway.Rest : rest).DeleteMessageAsync(player.TextChannelId, id);
        }
        catch (RestException)
        {
        }
    }

    private static Dictionary<string, string> Values(Track? track = null, ulong? voiceChannelId = null) => new()
    {
        ["track"] = track?.Markdown ?? "",
        ["user"] = track is null ? "" : $"<@{track.RequestedBy}>",
        ["channel"] = voiceChannelId is { } c ? $"<#{c}>" : "",
    };

}
