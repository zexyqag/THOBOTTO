using Microsoft.Extensions.Options;

using NetCord;
using NetCord.Rest;

using THOBOTTO.Modules;
using THOBOTTO.Voice;

namespace THOBOTTO.Music;

// Runs the helper bots and their players: hands out a helper per voice channel, posts what's
// playing, and sends helpers home when nobody listens or nothing plays for a while.
public sealed class MusicService(
    IOptions<MusicOptions> options,
    IOptions<LavalinkOptions> lavalink,
    RestClient rest,
    VoicePresence presence,
    SettingsStore settings,
    TimeProvider time,
    ILoggerFactory loggers) : BackgroundService
{
    public const string ModuleId = "music";

    private static readonly TimeSpan IdleCheck = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger = loggers.CreateLogger<MusicService>();
    private readonly SemaphoreSlim _assign = new(1, 1);
    private List<HelperBot> _helpers = [];

    public IReadOnlyList<HelperBot> Helpers => _helpers;

    public LavalinkOptions Lavalink => lavalink.Value;

    public MusicPlayer? PlayerIn(ulong guildId, ulong voiceChannelId)
        => _helpers.Select(h => h.Players.GetValueOrDefault(guildId)).FirstOrDefault(p => p?.VoiceChannelId == voiceChannelId);

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

            var helpers = _helpers.Where(h => h.InGuild(guildId)).ToList();
            if (helpers.Count == 0)
                return (null, "No music helper is in this server yet. `/music helpers` has invite links.");
            if (helpers.FirstOrDefault(h => !h.Players.ContainsKey(guildId) && h.Lavalink.SessionId is not null) is not { } free)
                return (null, "Every music helper is busy in another channel.");

            if (!await free.JoinAsync(guildId, voiceChannelId))
            {
                await free.LeaveAsync(guildId);
                return (null, "The helper couldn't connect to that channel. Can it see it and connect there?");
            }

            var player = new MusicPlayer(free, guildId, voiceChannelId, textChannelId, time);
            player.Changed += OnChangedAsync;
            free.Players[guildId] = player;
            return (player, null);
        }
        finally
        {
            _assign.Release();
        }
    }

    public async Task DisconnectAsync(MusicPlayer player)
    {
        player.Helper.Players.TryRemove(player.GuildId, out _);
        await DeleteNowPlayingAsync(player);
        await player.Helper.LeaveAsync(player.GuildId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Helpers.Any(h => !string.IsNullOrWhiteSpace(h.Token)))
        {
            _logger.LogInformation("No music helpers configured; music is unavailable");
            return;
        }

        // Deployments leave unused helper slots empty.
        _helpers = options.Value.Helpers.Where(h => !string.IsNullOrWhiteSpace(h.Token)).Select(h => new HelperBot(h.Token, lavalink.Value, loggers.CreateLogger<HelperBot>())).ToList();
        var running = new List<Task>();
        foreach (var helper in _helpers)
        {
            helper.Lavalink.Event += e => OnLavalinkEventAsync(helper, e);
            await helper.Gateway.StartAsync(cancellationToken: stoppingToken);
            running.Add(helper.Lavalink.RunAsync(stoppingToken));
        }
        _logger.LogInformation("Started {Count} music helpers", _helpers.Count);

        using var timer = new PeriodicTimer(IdleCheck, time);
        try
        {
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
        finally
        {
            foreach (var helper in _helpers)
                await helper.DisposeAsync();
        }
        await Task.WhenAll(running);
    }

    private async Task LeaveIdleAsync()
    {
        var now = time.GetUtcNow();
        foreach (var player in _helpers.SelectMany(h => h.Players.Values).ToList())
        {
            var rules = await settings.GetAsync<MusicRules>(player.GuildId, ModuleId);
            var limit = TimeSpan.FromMinutes(rules.IdleMinutes);
            var listeners = presence.Snapshot(player.GuildId).Values.Count(p => p.ChannelId == player.VoiceChannelId && !p.IsBot);
            var nothingPlaying = player.Current is null && now - player.IdleSince >= limit;
            if (listeners == 0 || nothingPlaying)
                player.LonelySince ??= now;
            else
                player.LonelySince = null;

            if (player.LonelySince is { } since && (nothingPlaying || now - since >= limit))
            {
                await DisconnectAsync(player);
                await SayAsync(player.TextChannelId, nothingPlaying ? "Nothing left to play, so I'm off. 👋" : "Nobody's listening, so I'm off. 👋");
            }
        }
    }

    private async Task OnLavalinkEventAsync(HelperBot helper, LavalinkEvent e)
    {
        if (!helper.Players.TryGetValue(e.GuildId, out var player))
            return;

        switch (e.Type)
        {
            case "TrackEndEvent":
                await player.TrackEndedAsync(e.Reason);
                break;
            case "TrackExceptionEvent":
                // Lavalink's message carries a stack trace after the first line.
                var reason = e.Message?.Split('\n')[0].Trim() ?? "unknown error";
                await SayAsync(player.TextChannelId, $"Couldn't play **{player.Current?.Title ?? "that"}**: {(reason.Length > 200 ? reason[..200] + "…" : reason)}");
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

    private async Task OnChangedAsync(MusicPlayer player, Track? track)
    {
        await DeleteNowPlayingAsync(player);
        if (track is null)
            return;

        try
        {
            var message = await rest.SendMessageAsync(player.TextChannelId, NowPlaying(player, track));
            player.NowPlayingMessageId = message.Id;
        }
        catch (RestException ex)
        {
            _logger.LogWarning("Posting now playing failed: {Message}", ex.Message);
        }
    }

    public static MessageProperties NowPlaying(MusicPlayer player, Track track) => new()
    {
        Embeds = [new()
        {
            Description = $"🎵 {track.Markdown}\n{track.Author} · {track.Length} · asked for by <@{track.RequestedBy}>"
                + (player.Queue.Count > 0 ? $"\nUp next: {player.Queue[0].Title}{(player.Queue.Count > 1 ? $" (+{player.Queue.Count - 1} more)" : "")}" : "")
                + $"\n-# Playing in <#{player.VoiceChannelId}> as {player.Helper.Name}{(player.Loop != LoopMode.Off ? $" · loop: {player.Loop.ToString().ToLowerInvariant()}" : "")}",
            Color = new(0x1DB954),
        }],
        Components = [new ActionRowProperties
        {
            new ButtonProperties($"music:pause:{player.Helper.UserId}", EmojiProperties.Standard("⏯️"), ButtonStyle.Secondary),
            new ButtonProperties($"music:skip:{player.Helper.UserId}", EmojiProperties.Standard("⏭️"), ButtonStyle.Secondary),
            new ButtonProperties($"music:stop:{player.Helper.UserId}", EmojiProperties.Standard("⏹️"), ButtonStyle.Secondary),
        }],
        AllowedMentions = AllowedMentionsProperties.None,
    };

    private async Task DeleteNowPlayingAsync(MusicPlayer player)
    {
        if (player.NowPlayingMessageId is not { } id)
            return;
        player.NowPlayingMessageId = null;
        try
        {
            await rest.DeleteMessageAsync(player.TextChannelId, id);
        }
        catch (RestException)
        {
        }
    }

    private async Task SayAsync(ulong channelId, string text)
    {
        try
        {
            await rest.SendMessageAsync(channelId, new() { Content = text, AllowedMentions = AllowedMentionsProperties.None });
        }
        catch (RestException ex)
        {
            _logger.LogWarning("Posting to {ChannelId} failed: {Message}", channelId, ex.Message);
        }
    }
}
