using System.Collections.Concurrent;
using System.Threading.Channels;

using Microsoft.EntityFrameworkCore;

using NetCord.Gateway;
using NetCord.Gateway.Voice;

using THOBOTTO.Data;
using THOBOTTO.Helpers;
using THOBOTTO.Integrations;
using THOBOTTO.Modules;
using THOBOTTO.Music;
using THOBOTTO.Voice;

namespace THOBOTTO.Listening;

// Something a member said, as text.
public sealed record Heard(ulong GuildId, ulong ChannelId, ulong UserId, string Text);

// The listener helper's ears (the helper the owner picked; it plays only when nothing else can): it sits (muted) in a
// voice channel where music plays and someone who opted in is listening, and turns what those members say into text, a sentence at a time (Discord only sends audio
// while someone talks, so a pause ends a sentence). Everyone else's audio is dropped undecoded; audio and
// text are never stored.
public sealed class VoiceEars(
    GatewayClient gateway,
    HelperFleet fleet,
    IntegrationStore integrations,
    MusicService music,
    ListeningSeats seats,
    ModuleState modules,
    VoicePresence presence,
    ISpeechToText speech,
    PersonalityBook personalities,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time,
    ILogger<VoiceEars> logger) : BackgroundService
{
    public const string ModuleId = "listening";

    private static readonly TimeSpan Check = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(200);
    // This long without audio from someone ends their sentence.
    private const long PauseMs = 700;
    private const double ShortestSeconds = 0.4;
    private const double LongestSeconds = 15;
    private const int Rate = 48_000;
    private const int MaxFrameSamples = 5760;

    private readonly ConcurrentDictionary<ulong, Connection> _connections = new();
    private readonly Channel<(ulong Guild, ulong Channel, ulong User, float[] Audio)> _sentences =
        Channel.CreateBounded<(ulong, ulong, ulong, float[])>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest });
    private volatile Dictionary<ulong, HashSet<ulong>> _optedIn = [];

    public event Func<Heard, Task>? Heard;

    // Where it listens now, per guild.
    public IReadOnlyDictionary<ulong, ulong> Listening => _connections.ToDictionary(c => c.Key, c => c.Value.ChannelId);

    public async Task<bool> IsInAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ListeningOptIns.AnyAsync(o => o.GuildId == guildId && o.UserId == userId);
    }

    public async Task SetInAsync(ulong guildId, ulong userId, bool listen)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!listen)
            await db.ListeningOptIns.Where(o => o.GuildId == guildId && o.UserId == userId).ExecuteDeleteAsync();
        else if (!await db.ListeningOptIns.AnyAsync(o => o.GuildId == guildId && o.UserId == userId))
        {
            db.ListeningOptIns.Add(new() { GuildId = guildId, UserId = userId, At = time.GetUtcNow() });
            await db.SaveChangesAsync();
        }
        await LoadOptInsAsync();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var worker = TranscribeAsync(stoppingToken);
        var nextCheck = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(Tick, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            FlushPauses();
            if (time.GetUtcNow() < nextCheck)
                continue;
            nextCheck = time.GetUtcNow() + Check;
            try
            {
                await LoadOptInsAsync();
                foreach (var guild in gateway.Cache.Guilds.Values.ToList())
                    await ReconcileAsync(guild.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Deciding where to listen failed");
            }
        }
        await worker;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        foreach (var guildId in _connections.Keys.ToList())
            await LeaveAsync(guildId);
    }

    private async Task LoadOptInsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        _optedIn = (await db.ListeningOptIns.AsNoTracking().ToListAsync()).GroupBy(o => o.GuildId).ToDictionary(g => g.Key, g => g.Select(o => o.UserId).ToHashSet());
    }

    // The helper set aside to listen, when it's in that server.
    public HelperBot? ListenerIn(ulong guildId)
        => fleet.Helpers.FirstOrDefault(h => h.UserId.ToString() == integrations.Get(IntegrationStore.Listener) && h.InGuild(guildId));

    // Listens where music plays and an opted-in member is (not deafened); else nowhere.
    private async Task ReconcileAsync(ulong guildId)
    {
        ulong? wanted = null;
        var listener = ListenerIn(guildId);
        // While it plays music somewhere in this server, it can't listen here (one voice connection per server).
        if (listener is not null && !listener.Players.ContainsKey(guildId) && await modules.IsEnabledAsync(guildId, ModuleId) && _optedIn.TryGetValue(guildId, out var members))
        {
            var here = presence.Snapshot(guildId).Where(p => !p.Value.IsBot && !p.Value.Deafened && members.Contains(p.Key)).Select(p => p.Value.ChannelId).ToHashSet();
            wanted = music.PlayersIn(guildId).SelectMany(p => p.Channels).FirstOrDefault(here.Contains) is var channel and > 0 ? channel : null;
        }

        var current = _connections.GetValueOrDefault(guildId);
        if (current?.ChannelId == wanted && current?.Gateway == listener?.Gateway)
            return;
        if (current is not null)
            await LeaveAsync(guildId);
        if (wanted is { } channelId)
            await JoinAsync(listener!, guildId, channelId);
    }

    private async Task JoinAsync(HelperBot listener, ulong guildId, ulong channelId)
    {
        try
        {
            var client = await listener.Gateway.JoinVoiceChannelAsync(guildId, channelId, new VoiceClientConfiguration());
            var connection = new Connection(listener.Gateway, client, channelId, listener.UserId);
            client.VoiceReceive += args =>
            {
                Receive(guildId, connection, args);
                return default;
            };
            await client.StartAsync();
            // Muted: it only listens.
            await listener.Gateway.UpdateVoiceStateAsync(new VoiceStateProperties(guildId, channelId) { SelfMute = true });
            _connections[guildId] = connection;
            seats.Sit(guildId, connection.HelperId);
            logger.LogInformation("Listening in {ChannelId}", channelId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Joining {ChannelId} to listen failed: {Message}", channelId, ex.Message);
        }
    }

    private async Task LeaveAsync(ulong guildId)
    {
        if (!_connections.TryRemove(guildId, out var connection))
            return;
        seats.Leave(guildId);
        connection.Client.Dispose();
        foreach (var sentence in connection.Speaking.Values)
            sentence.Decoder.Dispose();
        try
        {
            await connection.Gateway.UpdateVoiceStateAsync(new VoiceStateProperties(guildId, null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug("Leaving voice in {GuildId}: {Message}", guildId, ex.Message);
        }
    }

    // Decodes as it arrives, only for members who opted in.
    private void Receive(ulong guildId, Connection connection, VoiceReceiveEventArgs args)
    {
        if (!connection.Client.Cache.SsrcUsers.TryGetValue(args.Ssrc, out var userId)
            || !_optedIn.TryGetValue(guildId, out var members) || !members.Contains(userId))
            return;
        var sentence = connection.Speaking.GetOrAdd(args.Ssrc, _ => new(userId, new OpusDecoder(VoiceChannels.Mono)));
        Span<short> pcm = stackalloc short[MaxFrameSamples];
        lock (sentence)
        {
            try
            {
                var samples = sentence.Decoder.Decode(args.Frame, pcm, MaxFrameSamples, false);
                sentence.Samples.AddRange(pcm[..samples]);
                sentence.LastAt = time.GetTimestamp();
            }
            catch (OpusException)
            {
                // A damaged frame; the rest of the sentence still counts.
            }
        }
    }

    // Sentences whose speaker paused go off to be turned into text.
    private void FlushPauses()
    {
        foreach (var (guildId, connection) in _connections)
        {
            foreach (var sentence in connection.Speaking.Values)
            {
                short[] samples;
                lock (sentence)
                {
                    if (sentence.Samples.Count == 0 || time.GetElapsedTime(sentence.LastAt).TotalMilliseconds < PauseMs)
                        continue;
                    samples = [.. sentence.Samples];
                    sentence.Samples.Clear();
                }
                var seconds = samples.Length / (double)Rate;
                if (seconds is >= ShortestSeconds and <= LongestSeconds)
                    _sentences.Writer.TryWrite((guildId, connection.ChannelId, sentence.UserId, To16kHz(samples)));
            }
        }
    }

    // One at a time: speech to text is the heavy part.
    private async Task TranscribeAsync(CancellationToken ct)
    {
        await foreach (var (guildId, channelId, userId, audio) in _sentences.Reader.ReadAllAsync(ct))
        {
            try
            {
                var text = await speech.TranscribeAsync(audio, await HintAsync(guildId), ct);
                if (text.Length == 0 || Heard is not { } heard)
                    continue;
                foreach (var handler in heard.GetInvocationList().Cast<Func<Heard, Task>>())
                    await handler(new(guildId, channelId, userId, text));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Understanding a sentence failed");
            }
        }
    }

    // The names it should expect to hear: the helpers', as the server calls them.
    private async Task<string> HintAsync(ulong guildId)
    {
        var names = (await personalities.ListAsync(guildId)).Select(p => p.Name);
        return $"{string.Join(", ", names)}. Play, skip, pause, resume, stop, louder, quieter, what's playing.";
    }

    // 48 kHz to 16 kHz (averaging each three samples), as Whisper wants it.
    public static float[] To16kHz(short[] samples)
    {
        var result = new float[samples.Length / 3];
        for (var i = 0; i < result.Length; i++)
            result[i] = (samples[3 * i] + samples[3 * i + 1] + samples[3 * i + 2]) / (3 * 32768f);
        return result;
    }

    private sealed class Connection(GatewayClient gateway, VoiceClient client, ulong channelId, ulong helperId)
    {
        public ulong HelperId { get; } = helperId;

        public GatewayClient Gateway { get; } = gateway;

        public VoiceClient Client { get; } = client;

        public ulong ChannelId { get; } = channelId;

        // Stream (ssrc) → the sentence being said on it.
        public ConcurrentDictionary<uint, Sentence> Speaking { get; } = new();
    }

    private sealed class Sentence(ulong userId, OpusDecoder decoder)
    {
        public ulong UserId { get; } = userId;

        public OpusDecoder Decoder { get; } = decoder;

        public List<short> Samples { get; } = [];

        public long LastAt { get; set; }
    }
}
