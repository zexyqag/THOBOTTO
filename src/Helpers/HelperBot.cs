using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;

using THOBOTTO.Music;
using THOBOTTO.Relay;

namespace THOBOTTO.Helpers;

// One helper bot account: its own gateway connection (to join voice and speak) and its own Lavalink link.
// Discord sends a voice session in two halves (our voice state, then the voice server); once both
// are in, they go to Lavalink, which connects and plays.
public sealed class HelperBot : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<ulong, VoiceSession> _voice = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource> _connected = new();

    private readonly VoiceRelay _relay;
    private readonly Func<bool> _relayOn;
    // Server → its voice connection and relay session, when it plays through the relay.
    private readonly ConcurrentDictionary<ulong, (VoiceClient Client, RelaySession Session)> _relayed = new();

    public HelperBot(string token, LavalinkOptions lavalink, VoiceRelay relay, Func<bool> relayOn, ILogger logger)
    {
        (_relay, _relayOn) = (relay, relayOn);
        _logger = logger;
        var botToken = new BotToken(token);
        UserId = botToken.Id;
        Gateway = new GatewayClient(botToken, new GatewayClientConfiguration { Intents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates });
        Lavalink = new LavalinkConnection(lavalink, UserId, logger);
        Gateway.VoiceStateUpdate += OnVoiceStateAsync;
        Gateway.VoiceServerUpdate += OnVoiceServerAsync;
        Lavalink.Ready += OnLavalinkReadyAsync;
    }

    public ulong UserId { get; }

    // Stopped when the helper is removed.
    public CancellationTokenSource Life { get; } = new();

    public GatewayClient Gateway { get; }

    public LavalinkConnection Lavalink { get; }

    // Thrown out of voice by someone (disconnected, or the channel deleted), not leaving itself.
    public event Func<ulong, Task>? Disconnected;

    // Playing through the relay, its own connection can also hear the channel: (server, channel, connection), and
    // (server) when that ends.
    public event Func<ulong, ulong, VoiceClient, Task>? Hearing;

    public event Func<ulong, Task>? NotHearing;

    // Lavalink came back with a fresh session and has the voice sessions again; players restart their tracks.
    public event Func<Task>? Reconnected;

    // Guild → the player this helper runs there.
    public ConcurrentDictionary<ulong, MusicPlayer> Players { get; } = new();

    public string Name => Gateway.Cache.User?.Username ?? UserId.ToString();

    public bool InGuild(ulong guildId) => Gateway.Cache.Guilds.ContainsKey(guildId);

    // View Channel, Send Messages, Embed Links, Connect, Speak, Change Nickname: what a helper uses.
    private const ulong InvitePermissions = 1024 | 2048 | 16384 | 1048576 | 2097152 | 67108864;

    // Adds it to a server; someone with Manage Server there approves.
    public string InviteUrl(ulong guildId)
        => $"https://discord.com/oauth2/authorize?client_id={UserId}&scope=bot&permissions={InvitePermissions}&guild_id={guildId}&disable_guild_select=true";

    // Joins (or moves to) a voice channel and waits until Lavalink has the voice session.
    public async Task<bool> JoinAsync(ulong guildId, ulong channelId)
    {
        if (_relayOn())
            return await JoinThroughRelayAsync(guildId, channelId);
        var connected = _connected[guildId] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _voice[guildId] = new(channelId, null, null, null);
        await Gateway.UpdateVoiceStateAsync(new(guildId, channelId) { SelfDeaf = true });
        return await Task.WhenAny(connected.Task, Task.Delay(TimeSpan.FromSeconds(15))) == connected.Task;
    }

    // The connection to Discord is its own (NetCord's, which can also listen); Lavalink plays into the relay,
    // which hands each frame on.
    private async Task<bool> JoinThroughRelayAsync(ulong guildId, ulong channelId)
    {
        await StopRelayingAsync(guildId);
        try
        {
            _voice[guildId] = new(channelId, null, null, null, Relayed: true);
            var client = await Gateway.JoinVoiceChannelAsync(guildId, channelId, new VoiceClientConfiguration());
            await client.StartAsync();
            await client.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone));
            var session = _relay.Open((sequence, timestamp, frame) => client.SendVoice(sequence, timestamp, frame.Span));
            _relayed[guildId] = (client, session);
            await PointAtRelayAsync(guildId, channelId, session);
            if (Hearing is { } hearing)
                await hearing(guildId, channelId, client);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Joining {ChannelId} through the relay failed: {Message}", channelId, ex.Message);
            await StopRelayingAsync(guildId);
            return false;
        }
    }

    private Task PointAtRelayAsync(ulong guildId, ulong channelId, RelaySession session)
        => Lavalink.UpdatePlayerAsync(guildId, new JsonObject
        {
            ["voice"] = new JsonObject { ["token"] = session.Token, ["endpoint"] = _relay.Endpoint, ["sessionId"] = session.Token, ["channelId"] = channelId.ToString() },
        });

    private async Task StopRelayingAsync(ulong guildId)
    {
        if (!_relayed.TryRemove(guildId, out var relayed))
            return;
        _relay.Close(relayed.Session);
        relayed.Client.Dispose();
        if (NotHearing is { } notHearing)
            await notHearing(guildId);
    }

    public async Task LeaveAsync(ulong guildId)
    {
        _voice.TryRemove(guildId, out _);
        await StopRelayingAsync(guildId);
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
            _voice.TryRemove(state.GuildId, out _);
            await StopRelayingAsync(state.GuildId);
            if (Disconnected is { } disconnected)
                await disconnected(state.GuildId);
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

    private async Task OnLavalinkReadyAsync()
    {
        if (_voice.IsEmpty)
            return;
        foreach (var (guildId, session) in _voice)
        {
            if (_relayed.TryGetValue(guildId, out var relayed))
                await PointAtRelayAsync(guildId, session.ChannelId, relayed.Session);
            else
                await SendVoiceAsync(guildId, session);
        }
        if (Reconnected is { } reconnected)
            await reconnected();
    }

    private async Task SendVoiceAsync(ulong guildId, VoiceSession session)
    {
        // Through the relay, NetCord holds the Discord side; Lavalink only ever gets the relay.
        if (session is not { Relayed: false, SessionId: { } sessionId, Token: { } token, Endpoint: { } endpoint })
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
        await Life.CancelAsync();
        Gateway.Dispose();
    }

    private sealed record VoiceSession(ulong ChannelId, string? SessionId, string? Token, string? Endpoint, bool Relayed = false);
}
