using System.Net;

using Microsoft.Extensions.Options;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Access;
using THOBOTTO.Helpers;
using THOBOTTO.Modules;
using THOBOTTO.Voice;

namespace THOBOTTO.Music;

// Music players on the helper bots: hands out a helper per voice channel, syncs more helpers into
// other channels to play along, lets helpers speak as the personality they wear, and sends them home
// when nobody listens or nothing plays for a while.
public sealed class MusicService(
    HelperFleet fleet,
    IOptions<LavalinkOptions> lavalink,
    RestClient rest,
    GatewayClient gateway,
    VoicePresence presence,
    SettingsStore settings,
    PersonalityBook personalities,
    AccessControl access,
    TimeProvider time,
    ILoggerFactory loggers) : BackgroundService, IHelperAware
{
    public const string ModuleId = "music";

    private static readonly TimeSpan IdleCheck = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger = loggers.CreateLogger<MusicService>();
    private readonly SemaphoreSlim _assign = new(1, 1);
    private IReadOnlyList<HelperBot> _helpers => fleet.Helpers;

    public IReadOnlyList<HelperBot> Helpers => _helpers;

    public LavalinkOptions Lavalink => lavalink.Value;

    public MusicPlayer? PlayerIn(ulong guildId, ulong voiceChannelId)
        => _helpers.Select(h => h.Players.GetValueOrDefault(guildId)).FirstOrDefault(p => p?.Plays(voiceChannelId) == true);

    // The player in that voice channel, or a free helper sent there. Null with a reason when none is free.
    public async Task<(MusicPlayer? Player, string? Problem)> PlayerForAsync(ulong guildId, ulong voiceChannelId, ulong textChannelId)
    {
        await _assign.WaitAsync();
        try
        {
            if (PlayerIn(guildId, voiceChannelId) is { } existing)
            {
                existing.TextChannelId = textChannelId;
                return (existing, null);
            }

            var (free, problem) = await SendHelperAsync(guildId, voiceChannelId);
            if (free is null)
                return (null, problem);

            var player = new MusicPlayer(free, guildId, voiceChannelId, textChannelId, time);
            player.Changed += OnChangedAsync;
            free.Players[guildId] = player;
            await SpeakAsync(guildId, free, textChannelId, Moments.Joined, Values(voiceChannelId: voiceChannelId));
            return (player, null);
        }
        finally
        {
            _assign.Release();
        }
    }

    // Sends another helper to play along in that channel. Null, or why not.
    public async Task<string?> SyncAsync(MusicPlayer player, ulong voiceChannelId)
    {
        await _assign.WaitAsync();
        try
        {
            if (PlayerIn(player.GuildId, voiceChannelId) is { } there)
                return there == player ? "That channel already plays along." : "Other music plays there already.";

            var (free, problem) = await SendHelperAsync(player.GuildId, voiceChannelId);
            if (free is null)
                return problem;

            free.Players[player.GuildId] = player;
            await player.AddMirrorAsync(new(free, voiceChannelId));
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
        using var timer = new PeriodicTimer(IdleCheck, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await LeaveIdleAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Checking idle music players failed");
            }
        }
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
    private async Task<(HelperBot? Helper, string? Problem)> SendHelperAsync(ulong guildId, ulong voiceChannelId)
    {
        var helpers = _helpers.Where(h => h.InGuild(guildId)).ToList();
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
}
