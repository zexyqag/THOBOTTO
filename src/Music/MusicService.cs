using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Helpers;
using THOBOTTO.Modules;
using THOBOTTO.Voice;

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
    TimeProvider time,
    ILoggerFactory loggers) : BackgroundService, IHelperAware
{
    public const string ModuleId = "music";

    private static readonly TimeSpan IdleCheck = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger = loggers.CreateLogger<MusicService>();
    private readonly SemaphoreSlim _assign = new(1, 1);
    private bool _savedAny;
    private IReadOnlyList<HelperBot> _helpers => fleet.Helpers;

    public IReadOnlyList<HelperBot> Helpers => _helpers;

    public LavalinkOptions Lavalink => lavalink.Value;

    public MusicPlayer? PlayerIn(ulong guildId, ulong voiceChannelId)
        => _helpers.Select(h => h.Players.GetValueOrDefault(guildId)).FirstOrDefault(p => p?.Plays(voiceChannelId) == true);

    // Every player in the guild (a synced one once, though several helpers play it).
    public IReadOnlyList<MusicPlayer> PlayersIn(ulong guildId)
        => _helpers.Select(h => h.Players.GetValueOrDefault(guildId)).OfType<MusicPlayer>().Distinct().ToList();

    // Loads what was asked for and queues it in the voice channel, bringing a helper if none plays there.
    // Returns the reply: what plays or got queued, or why not.
    public async Task<string> PlayAsync(ulong guildId, ulong userId, ulong voiceChannelId, ulong textChannelId, string query)
    {
        var rules = await settings.GetAsync<MusicRules>(guildId, ModuleId);
        // Links and explicit sources ("scsearch:", "ytsearch:", …) go as typed; plain words to the default search.
        var text = query.Trim();
        var asTyped = Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" || SourcePrefix().IsMatch(text);
        var identifier = asTyped ? text : $"{rules.DefaultSearch}:{text}";

        LoadResult loaded;
        try
        {
            loaded = await LavalinkConnection.LoadAsync(Lavalink, identifier, userId);
        }
        catch (HttpRequestException)
        {
            return "The music server isn't reachable right now.";
        }

        if (loaded.Error is not null || loaded.Tracks.Count == 0)
            return loaded.Error is null ? "Nothing found." : $"Couldn't load that: {loaded.Error}";

        var (player, problem) = await PlayerForAsync(guildId, voiceChannelId, textChannelId);
        if (player is null)
            return problem!;

        // A search plays its best match; a playlist goes in whole, up to the queue limit.
        var room = Math.Max(0, rules.MaxQueue - player.Queue.Count);
        var tracks = (loaded.Playlist is null ? loaded.Tracks.Take(1) : loaded.Tracks.Take(room)).ToList();
        if (tracks.Count == 0)
            return $"The queue is full ({rules.MaxQueue}).";

        var position = await player.EnqueueAsync(tracks);
        var what = loaded.Playlist is { } name ? $"**{tracks.Count}** tracks from **{name}**" : tracks[0].Markdown;
        return position == 0 ? $"▶️ {what}" : $"➕ Queued {what} (#{position})";
    }

    // The player in that voice channel, or a free helper sent there (the preferred one when it's free).
    // Null with a reason when none is free. Quiet: no "joined" line, as when picking up after a restart.
    public async Task<(MusicPlayer? Player, string? Problem)> PlayerForAsync(ulong guildId, ulong voiceChannelId, ulong textChannelId, ulong? preferredHelper = null, bool quiet = false)
    {
        await _assign.WaitAsync();
        try
        {
            if (PlayerIn(guildId, voiceChannelId) is { } existing)
            {
                existing.TextChannelId = textChannelId;
                return (existing, null);
            }

            var (free, problem) = await SendHelperAsync(guildId, voiceChannelId, preferredHelper);
            if (free is null)
                return (null, problem);

            var player = new MusicPlayer(free, guildId, voiceChannelId, textChannelId, time);
            player.Changed += OnChangedAsync;
            free.Players[guildId] = player;
            if (!quiet)
                await SpeakAsync(guildId, free, textChannelId, Moments.Joined, Values(voiceChannelId: voiceChannelId));
            return (player, null);
        }
        finally
        {
            _assign.Release();
        }
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

            free.Players[player.GuildId] = player;
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
        mirror.Helper.Players.TryRemove(player.GuildId, out _);
        await mirror.Helper.LeaveAsync(player.GuildId);
    }

    public async Task DisconnectAsync(MusicPlayer player)
    {
        foreach (var mirror in player.Mirrors)
            await UnsyncAsync(player, mirror);
        player.Helper.Players.TryRemove(player.GuildId, out _);
        await DeleteNowPlayingAsync(player);
        await player.Helper.LeaveAsync(player.GuildId);
    }

    // Why the user may not control that player, or null when they may.
    public async Task<string?> RefusalAsync(ulong guildId, GuildUser user, MusicPlayer player, bool ownTrackAllowed = false)
    {
        var rules = await settings.GetAsync<MusicRules>(guildId, ModuleId);
        var own = ownTrackAllowed && player.Current?.RequestedBy == user.Id;
        return rules.DjOnly && !own && gateway.Cache.Guilds.TryGetValue(guildId, out var guild) && !await access.CanAsync(guild, user, BotPermissions.MusicDj)
            ? $"That needs `{BotPermissions.MusicDj}` here."
            : null;
    }

    // A helper's line for a moment, prefixed with its name for posts the main bot makes on its behalf.
    public async Task<string> LineAsync(ulong guildId, HelperBot helper, string moment, Track? track = null)
        => $"**{await personalities.NameAsync(guildId, helper)}:** {await personalities.SayAsync(guildId, helper, moment, Values(track))}";

    // The now-playing buttons, wherever they were pressed (helpers post their own messages).
    public async Task<string> ButtonAsync(ulong guildId, GuildUser user, string action, ulong helperId)
    {
        var helper = _helpers.FirstOrDefault(h => h.UserId == helperId);
        if (helper?.Players.GetValueOrDefault(guildId) is not { } player)
            return "That player has stopped.";

        if (await RefusalAsync(guildId, user, player, ownTrackAllowed: action == "skip") is { } refusal)
            return refusal;

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

    // A helper being removed stops what it plays, or stops playing along.
    public async Task DetachAsync(HelperBot helper)
    {
        foreach (var (guildId, player) in helper.Players.ToList())
            await OnThrownOutAsync(helper, guildId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Saving waits for this, so what's saved isn't overwritten before it's back.
        await RestoreAllAsync(stoppingToken);

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
        foreach (var player in _helpers.SelectMany(h => h.Players.Values).Distinct().ToList())
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
            && _helpers.Any(h => h.UserId == s.State.HelperId && h.InGuild(s.Row.GuildId) && h.Lavalink.SessionId is not null)))
            await Task.Delay(TimeSpan.FromSeconds(1), time, ct);

        foreach (var (row, state) in states)
        {
            try
            {
                await RestoreAsync(row.GuildId, state);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Picking the music in {ChannelId} up again failed", state.VoiceChannelId);
            }
        }
    }

    private async Task RestoreAsync(ulong guildId, PlayerState state)
    {
        var listening = presence.Snapshot(guildId).Values.Where(p => !p.IsBot).Select(p => p.ChannelId).ToHashSet();
        if (!listening.Contains(state.VoiceChannelId) && !state.Mirrors.Any(m => listening.Contains(m.VoiceChannelId)))
            return;

        var (player, problem) = await PlayerForAsync(guildId, state.VoiceChannelId, state.TextChannelId, state.HelperId, quiet: true);
        if (player is null)
        {
            _logger.LogInformation("Music in {ChannelId} didn't come back: {Problem}", state.VoiceChannelId, problem);
            return;
        }
        await player.RestoreAsync(state);
        foreach (var mirror in state.Mirrors.Where(m => listening.Contains(m.VoiceChannelId)))
            await SyncAsync(player, mirror.VoiceChannelId, mirror.HelperId, quiet: true);
        _logger.LogInformation("Music in {ChannelId} picked up again", state.VoiceChannelId);
    }

    private async Task LeaveIdleAsync()
    {
        var now = time.GetUtcNow();
        foreach (var player in _helpers.SelectMany(h => h.Players.Values).Distinct().ToList())
        {
            var rules = await settings.GetAsync<MusicRules>(player.GuildId, ModuleId);
            var limit = TimeSpan.FromMinutes(rules.IdleMinutes);
            var present = presence.Snapshot(player.GuildId).Values.Where(p => !p.IsBot).ToList();

            // A channel playing along with nobody in it goes on its own; the queue stays while anyone listens anywhere.
            foreach (var mirror in player.Mirrors)
            {
                if (present.Any(p => p.ChannelId == mirror.VoiceChannelId))
                    mirror.LonelySince = null;
                else if ((mirror.LonelySince ??= now) <= now - limit)
                {
                    await UnsyncAsync(player, mirror);
                    await SpeakAsync(player.GuildId, mirror.Helper, player.TextChannelId, Moments.Lonely, Values());
                }
            }

            var listeners = present.Count(p => player.Plays(p.ChannelId));
            var nothingPlaying = player.Current is null && now - player.IdleSince >= limit;
            if (listeners == 0 || nothingPlaying)
                player.LonelySince ??= now;
            else
                player.LonelySince = null;

            if (player.LonelySince is { } since && (nothingPlaying || now - since >= limit))
            {
                await DisconnectAsync(player);
                await SpeakAsync(player.GuildId, player.Helper, player.TextChannelId, nothingPlaying ? Moments.Finished : Moments.Lonely, Values());
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
        if (!helper.Players.TryGetValue(e.GuildId, out var player))
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
                await player.TrackEndedAsync(e.Reason);
                break;
            case "TrackExceptionEvent":
                // Lavalink's message carries a stack trace after the first line.
                var reason = e.Message?.Split('\n')[0].Trim() ?? "unknown error";
                await PostAsync(player.GuildId, helper, player.TextChannelId, new()
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
        if (!helper.Players.TryGetValue(guildId, out var player))
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

    private async Task OnReconnectedAsync(HelperBot helper)
    {
        foreach (var player in helper.Players.Values)
            await player.ResumeAsync(helper);
    }

    private async Task OnPositionAsync(HelperBot helper, LavalinkPosition update)
    {
        if (helper.Players.TryGetValue(update.GuildId, out var player) && await player.PositionAsync(helper, update.Time, update.Position) is { } drift)
            _logger.LogDebug("{Helper} was {Drift} ms off in {GuildId}; caught up", helper.Name, drift, update.GuildId);
    }

    private async Task OnChangedAsync(MusicPlayer player, Track? track)
    {
        await DeleteNowPlayingAsync(player);
        if (track is null)
            return;

        var helper = player.Helper;
        var line = await personalities.SayAsync(player.GuildId, helper, Moments.Playing, Values(track));
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

        if (await PostAsync(player.GuildId, helper, player.TextChannelId, message) is { } posted)
            (player.NowPlayingMessageId, player.NowPlayingByHelper) = posted;
    }

    // A free helper, connected to that channel; else null and why.
    private async Task<(HelperBot? Helper, string? Problem)> SendHelperAsync(ulong guildId, ulong voiceChannelId, ulong? preferred = null)
    {
        var helpers = _helpers.Where(h => h.InGuild(guildId)).OrderBy(h => h.UserId == preferred ? 0 : 1).ToList();
        if (helpers.Count == 0)
            return (null, "No helper bot is in this server yet. `/music helpers` has invite links.");
        if (helpers.FirstOrDefault(h => !h.Players.ContainsKey(guildId) && h.Lavalink.SessionId is not null) is not { } free)
            return (null, "Every helper bot is busy in another channel.");

        if (await free.JoinAsync(guildId, voiceChannelId))
            return (free, null);
        await free.LeaveAsync(guildId);
        return (null, "The helper couldn't connect to that channel. Can it see it and connect there?");
    }

    // Says a moment's line as the helper.
    private async Task SpeakAsync(ulong guildId, HelperBot helper, ulong channelId, string moment, IReadOnlyDictionary<string, string> values)
    {
        var line = await personalities.SayAsync(guildId, helper, moment, values);
        await PostAsync(guildId, helper, channelId, new() { Content = line, AllowedMentions = AllowedMentionsProperties.None });
    }

    // Posts as the helper; where it may not post, the main bot posts for it, under its name.
    private async Task<(ulong Id, bool ByHelper)?> PostAsync(ulong guildId, HelperBot helper, ulong channelId, MessageProperties message)
    {
        try
        {
            return ((await helper.Gateway.Rest.SendMessageAsync(channelId, message)).Id, true);
        }
        catch (RestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            var name = await personalities.NameAsync(guildId, helper);
            message.Content = message.Content is { } content ? $"**{name}:** {content}" : $"**{name}**";
        }

        try
        {
            return ((await rest.SendMessageAsync(channelId, message)).Id, false);
        }
        catch (RestException ex)
        {
            _logger.LogWarning("Posting to {ChannelId} failed: {Message}", channelId, ex.Message);
            return null;
        }
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

    [GeneratedRegex(@"^[a-z]{2,5}search:")]
    private static partial Regex SourcePrefix();
}
