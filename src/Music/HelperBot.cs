using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using NetCord;
using NetCord.Gateway;

namespace THOBOTTO.Music;

// One helper bot account: its own gateway connection (to join voice) and its own Lavalink link.
// Discord sends a voice session in two halves (our voice state, then the voice server); once both
// are in, they go to Lavalink, which connects and plays.
public sealed class HelperBot : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<ulong, VoiceSession> _voice = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource> _connected = new();

    public HelperBot(string token, LavalinkOptions lavalink, ILogger logger)
    {
        _logger = logger;
        var botToken = new BotToken(token);
        UserId = botToken.Id;
        Gateway = new GatewayClient(botToken, new GatewayClientConfiguration { Intents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates });
        Lavalink = new LavalinkConnection(lavalink, UserId, logger);
        Gateway.VoiceStateUpdate += OnVoiceStateAsync;
        Gateway.VoiceServerUpdate += OnVoiceServerAsync;
    }

    public ulong UserId { get; }

    public GatewayClient Gateway { get; }

    public LavalinkConnection Lavalink { get; }

    // Guild → the player this helper runs there.
    public ConcurrentDictionary<ulong, MusicPlayer> Players { get; } = new();

    public string Name => Gateway.Cache.User?.Username ?? UserId.ToString();

    public bool InGuild(ulong guildId) => Gateway.Cache.Guilds.ContainsKey(guildId);

    // Joins (or moves to) a voice channel and waits until Lavalink has the voice session.
    public async Task<bool> JoinAsync(ulong guildId, ulong channelId)
    {
        var connected = _connected[guildId] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _voice[guildId] = new(channelId, null, null, null);
        await Gateway.UpdateVoiceStateAsync(new(guildId, channelId) { SelfDeaf = true });
        return await Task.WhenAny(connected.Task, Task.Delay(TimeSpan.FromSeconds(15))) == connected.Task;
    }

    public async Task LeaveAsync(ulong guildId)
    {
        _voice.TryRemove(guildId, out _);
        await Gateway.UpdateVoiceStateAsync(new(guildId, null));
        if (Lavalink.SessionId is not null)
        {
            try
            {
                await Lavalink.DestroyPlayerAsync(guildId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Destroying player in {GuildId}: {Message}", guildId, ex.Message);
            }
        }
    }

    private async ValueTask OnVoiceStateAsync(VoiceState state)
    {
        if (state.UserId != UserId || !_voice.TryGetValue(state.GuildId, out var session))
            return;
        if (state.ChannelId is not { } channelId)
        {
            // Disconnected, e.g. moved out by someone; the player's owner notices on its next use.
            _voice.TryRemove(state.GuildId, out _);
            return;
        }
        _voice[state.GuildId] = session = session with { ChannelId = channelId, SessionId = state.SessionId };
        await SendVoiceAsync(state.GuildId, session);
    }

    private async ValueTask OnVoiceServerAsync(VoiceServerUpdateEventArgs args)
    {
        if (args.Endpoint is null || !_voice.TryGetValue(args.GuildId, out var session))
            return;
        _voice[args.GuildId] = session = session with { Token = args.Token, Endpoint = args.Endpoint };
        await SendVoiceAsync(args.GuildId, session);
    }

    private async Task SendVoiceAsync(ulong guildId, VoiceSession session)
    {
        if (session is not { SessionId: { } sessionId, Token: { } token, Endpoint: { } endpoint })
            return;

        try
        {
            await Lavalink.UpdatePlayerAsync(guildId, new JsonObject
            {
                ["voice"] = new JsonObject
                {
                    ["token"] = token,
                    ["endpoint"] = endpoint,
                    ["sessionId"] = sessionId,
                    ["channelId"] = session.ChannelId.ToString(),
                },
            });
            if (_connected.TryRemove(guildId, out var connected))
                connected.TrySetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Handing the voice session in {GuildId} to Lavalink failed", guildId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var guildId in _voice.Keys)
            await LeaveAsync(guildId);
        Gateway.Dispose();
    }

    private sealed record VoiceSession(ulong ChannelId, string? SessionId, string? Token, string? Endpoint);
}
