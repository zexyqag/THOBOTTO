using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;

using Microsoft.EntityFrameworkCore;

using NetCord.Gateway;
using NetCord.Gateway.Voice;

using THOBOTTO.Data;
using THOBOTTO.Debugging;
using THOBOTTO.Discord;
using THOBOTTO.Helpers;
using THOBOTTO.Modules;

namespace THOBOTTO.Voice.Hearing;

// Something a member said, as text, and which helper heard it.
public sealed record Heard(ulong GuildId, ulong ChannelId, ulong UserId, ulong ListenerId, string Text, TraceSentence? Trace = null);

// Helpers' ears. A free helper (one not playing in that server) sits muted in a voice channel when someone
// there summoned it (/listen) or has it join them automatically, and turns what members who let it hear them
// say into text, a sentence at a time (Discord only sends audio while someone talks, so a pause ends one).
// Everyone else's audio is dropped undecoded; audio and text are never stored. When helpers run out, the
// server's priority decides between music and listening.
public sealed partial class VoiceEars(
    GatewayClient gateway,
    HelperFleet fleet,
    IHelperWork work,
    ListeningSeats seats,
    VoicePrefs prefs,
    ModuleState modules,
    SettingsStore settings,
    VoicePresence presence,
    ISpeechToText speech,
    PersonalityBook personalities,
    IDbContextFactory<BotDbContext> dbFactory,
    VoiceMouths mouths,
    VoiceTrace trace,
    TimeProvider time,
    ILogger<VoiceEars> logger) : BackgroundService, IHelperAware
{
    public const string ModuleId = "voice-commands";

    private static readonly TimeSpan Check = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(200);
    // A summoned helper stays this long after the last member who lets it hear them left.
    private static readonly TimeSpan SummonGrace = TimeSpan.FromMinutes(1);
    // This long without audio from someone ends their sentence.
    private const long PauseMs = 700;
    private const double ShortestSeconds = 0.6;
    private const double LongestSeconds = 15;
    private const int Rate = 48_000;
    private const int MaxFrameSamples = 5760;

    // Voice channel → the helper listening there.
    private readonly ConcurrentDictionary<ulong, Connection> _connections = new();
    // Voice channel → a helper playing there through the relay, whose own connection hears it too.
    private readonly ConcurrentDictionary<ulong, Connection> _borrowed = new();
    // Voice channel → (server, since when nobody it may hear is there) for channels a member summoned a helper to.
    private readonly ConcurrentDictionary<ulong, (ulong Guild, DateTimeOffset? EmptySince)> _summoned = new();
    private readonly Channel<(ulong Guild, ulong Channel, ulong User, ulong Listener, float[] Audio, TraceSentence? Trace)> _sentences =
        Channel.CreateBounded<(ulong, ulong, ulong, ulong, float[], TraceSentence?)>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest });
    private volatile IReadOnlySet<(ulong Guild, ulong User)> _mayHear = new HashSet<(ulong, ulong)>();
    // Servers with voice commands on.
    private readonly ConcurrentDictionary<ulong, bool> _on = new();
    // Server → its bots, whose audio doesn't count as people talking.
    private readonly ConcurrentDictionary<ulong, IReadOnlySet<ulong>> _bots = new();
    // Talking counts this long after the last audio.
    private const long TalkingMs = 800;
    // Discord sends a packet per 20 ms while someone talks.
    private const long FrameMs = 20;
    private static readonly TimeSpan TalkFlush = TimeSpan.FromMinutes(1);
    // Server → how it counts talk time.
    private readonly ConcurrentDictionary<ulong, string> _talkMode = new();
    // (server, member) → milliseconds talked since the last write.
    private readonly ConcurrentDictionary<(ulong Guild, ulong User), long> _talked = new();
    private DateTimeOffset _talkFlushedAt;
    // Server → the channels a listener is wanted in, as last decided.
    private readonly ConcurrentDictionary<ulong, IReadOnlySet<ulong>> _wanted = new();
    // What follows a sentence's text (the language model, the action, the reply), in order per channel.
    private readonly InOrder _acting = new();

    public event Func<Heard, Task>? Heard;

    public Task AttachAsync(HelperBot helper)
    {
        helper.Hearing += (guildId, channelId, client) => BorrowAsync(helper, guildId, channelId, client);
        helper.NotHearing += guildId =>
        {
            foreach (var (channelId, connection) in _borrowed.Where(b => b.Value.HelperId == helper.UserId && b.Value.GuildId == guildId).ToList())
            {
                _borrowed.TryRemove(channelId, out _);
                Unhear(connection);
                mouths.Unseat(connection.Client);
            }
            return Task.CompletedTask;
        };
        return Task.CompletedTask;
    }

    public Task DetachAsync(HelperBot helper) => Task.CompletedTask;

    // A helper playing through the relay: it hears the members there who let it, no other helper needed.
    private async Task BorrowAsync(HelperBot helper, ulong guildId, ulong channelId, VoiceClient client)
    {
        _borrowed[channelId] = Hear(new(helper.Gateway, client, guildId, channelId, helper.UserId));
        mouths.Seat(guildId, channelId, new(helper, client, Muted: false));
        if (_connections.ContainsKey(channelId))
            await LeaveAsync(channelId);
    }

    public int Listening => _connections.Count + _borrowed.Count;

    // Where helpers hear in that server: each voice channel, and the helper listening there.
    public IReadOnlyList<(ulong ChannelId, ulong HelperId)> ListeningIn(ulong guildId)
        => [.. _connections.Values.Concat(_borrowed.Values).Where(c => c.GuildId == guildId).Select(c => (c.ChannelId, c.HelperId))];

    public bool ListensIn(ulong channelId) => _connections.ContainsKey(channelId) || _borrowed.ContainsKey(channelId);

    // /listen: a helper comes to the member's channel (the one named, taking over from one listening there).
    // Returns the answer.
    public async Task<string> SummonAsync(ulong guildId, ulong userId, ulong? helperId = null)
    {
        if (!(await prefs.GetAsync(guildId, userId)).Listen)
            return "First let the helpers hear you: `/me voice-commands on`.";
        if (!presence.Snapshot(guildId).TryGetValue(userId, out var where))
            return "Join a voice channel first.";
        if (helperId is { } wanted)
        {
            if (fleet.Helpers.FirstOrDefault(h => h.UserId == wanted && h.InGuild(guildId)) is not { } named)
                return "That helper isn't in this server.";
            var name = await personalities.NameAsync(guildId, named);
            if (_borrowed.ContainsKey(where.ChannelId))
                return "The helper playing here hears you already.";
            if (_connections.TryGetValue(where.ChannelId, out var sitting) && sitting.HelperId == wanted)
                return $"🎙️ {name} is listening here already.";
            if (work.Busy(wanted, guildId))
                return $"{name} is playing music here.";
            if (seats.IsListening(guildId, wanted))
                return $"{name} is listening in another channel.";
            if (_connections.ContainsKey(where.ChannelId))
                await LeaveAsync(where.ChannelId);
            _summoned[where.ChannelId] = (guildId, null);
            await JoinAsync(named, guildId, where.ChannelId);
            return _connections.ContainsKey(where.ChannelId) ? $"🎙️ {name} is listening. Say its name and what to do." : $"{name} couldn't join your channel.";
        }
        if (_connections.ContainsKey(where.ChannelId) || _borrowed.ContainsKey(where.ChannelId))
            return "A helper is already listening here.";
        _summoned[where.ChannelId] = (guildId, null);
        await ReconcileAsync(guildId);
        return _connections.TryGetValue(where.ChannelId, out var connection)
            ? $"🎙️ <@{connection.HelperId}> is listening. Say its name and what to do."
            : "No helper is free to listen right now.";
    }

    public async Task<string> DismissAsync(ulong guildId, ulong userId)
    {
        if (!presence.Snapshot(guildId).TryGetValue(userId, out var where) || !_summoned.TryRemove(where.ChannelId, out _))
            return "No helper was summoned to your channel.";
        await ReconcileAsync(guildId);
        return _connections.ContainsKey(where.ChannelId) ? "Others here have a helper join them, so it stays." : "🎙️ The helper left.";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        seats.Release = ReleaseAsync;
        seats.HandOver = HandOver;
        seats.TakeBack = TakeBackAsync;
        var worker = TranscribeAsync(stoppingToken);
        var nextCheck = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(Tick, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                FlushPauses();
                await DuckAsync();
                if (time.GetUtcNow() - _talkFlushedAt >= TalkFlush)
                    await SaveTalkTimeAsync();
                if (time.GetUtcNow() < nextCheck)
                    continue;
                nextCheck = time.GetUtcNow() + Check;
                foreach (var guild in gateway.Cache.Guilds.Values.ToList())
                    await ReconcileAsync(guild.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "A listening tick failed");
            }
        }
        await worker;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await SaveTalkTimeAsync();
        foreach (var channelId in _connections.Keys.ToList())
            await LeaveAsync(channelId);
    }

    private async Task ReconcileAsync(ulong guildId)
    {
        var all = await prefs.AllAsync();
        _mayHear = all.Values.Where(p => p.Listen).Select(p => (p.GuildId, p.UserId)).ToHashSet();
        var on = _on[guildId] = await modules.IsEnabledAsync(guildId, ModuleId);
        _bots[guildId] = presence.Snapshot(guildId).Where(p => p.Value.IsBot).Select(p => p.Key).ToHashSet();
        var rules = await settings.GetAsync<ListeningRules>(guildId, ModuleId);
        _talkMode[guildId] = on ? rules.TalkTime : TalkTimeModes.Off;
        trace.Follow(guildId, on && rules.Debugging);
        var present = presence.Snapshot(guildId).Where(p => !p.Value.IsBot && !p.Value.Deafened).ToList();
        var now = time.GetUtcNow();

        // Summoned channels stay wanted while someone it may hear is there (and a minute after).
        var wanted = new HashSet<ulong>();
        foreach (var (channelId, (guild, emptySince)) in _summoned.Where(s => s.Value.Guild == guildId).ToList())
        {
            if (present.Any(p => p.Value.ChannelId == channelId && _mayHear.Contains((guildId, p.Key))))
                _summoned[channelId] = (guild, null);
            else if (emptySince is null)
                _summoned[channelId] = (guild, now);
            else if (now - emptySince > SummonGrace)
            {
                _summoned.TryRemove(channelId, out _);
                continue;
            }
            wanted.Add(channelId);
        }
        // Nothing comes to the AFK channel on its own.
        var afk = gateway.Cache.Guilds.TryGetValue(guildId, out var cached) ? cached.AfkChannelId : null;
        // Counting talk time everywhere: a helper sits in each voice channel with people.
        if (rules.TalkTime == TalkTimeModes.Everywhere)
            wanted.UnionWith(present.Select(p => p.Value.ChannelId).Where(c => c != afk));
        if (rules.AutoListenAllowed)
        {
            foreach (var (userId, where) in present)
            {
                if (where.ChannelId != afk && all.GetValueOrDefault((guildId, userId)) is { Listen: true, AutoListen: true })
                    wanted.Add(where.ChannelId);
            }
        }
        if (!on)
            wanted.Clear();
        _wanted[guildId] = wanted;

        foreach (var (channelId, connection) in _connections.Where(c => c.Value.GuildId == guildId).ToList())
        {
            // A helper sent to play since doesn't listen any more; where the player hears, no listener is needed.
            if (!wanted.Contains(channelId) || _borrowed.ContainsKey(channelId) || work.Busy(connection.HelperId, guildId))
                await LeaveAsync(channelId);
        }
        foreach (var channelId in wanted.Where(c => !_connections.ContainsKey(c) && !_borrowed.ContainsKey(c)))
        {
            if (await HelperToListenAsync(guildId, channelId) is { } helper)
                await JoinAsync(helper, guildId, channelId);
        }
    }

    // A free helper, any of them; else one its work gives up, when the server puts listening first.
    private async Task<HelperBot?> HelperToListenAsync(ulong guildId, ulong channelId)
    {
        var free = fleet.Helpers.Where(h => h.InGuild(guildId) && !work.Busy(h.UserId, guildId) && !seats.IsListening(guildId, h.UserId)).ToList();
        if (free.Count > 0)
            return free[Random.Shared.Next(free.Count)];
        return await work.YieldAsync(guildId, channelId);
    }

    private async Task JoinAsync(HelperBot listener, ulong guildId, ulong channelId)
    {
        try
        {
            var client = await listener.Gateway.JoinVoiceChannelAsync(guildId, channelId, new VoiceClientConfiguration());
            await client.StartAsync();
            // Muted: it only listens.
            await listener.Gateway.UpdateVoiceStateAsync(new VoiceStateProperties(guildId, channelId) { SelfMute = true });
            Listen(listener, guildId, channelId, client);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Joining {ChannelId} in {GuildId} to listen failed: {Message}", channelId, guildId, ex.Message);
        }
    }

    private void Listen(HelperBot listener, ulong guildId, ulong channelId, VoiceClient client)
    {
        _connections[channelId] = Hear(new(listener.Gateway, client, guildId, channelId, listener.UserId));
        seats.Sit(guildId, listener.UserId, channelId);
        // A helper playing there through the relay speaks there; else this one does.
        if (!_borrowed.ContainsKey(channelId))
            mouths.Seat(guildId, channelId, new(listener, client, Muted: true));
        logger.LogInformation("{Helper} listening in {ChannelId} of {GuildId}", listener.Name, channelId, guildId);
    }

    private Connection Hear(Connection connection)
    {
        connection.Receiver = args =>
        {
            Receive(connection, args);
            return default;
        };
        connection.Client.VoiceReceive += connection.Receiver;
        return connection;
    }

    private static void Unhear(Connection connection)
    {
        connection.Client.VoiceReceive -= connection.Receiver;
        foreach (var sentence in connection.Speaking.Values)
            sentence.Decoder.Dispose();
        connection.Speaking.Clear();
    }

    // Music wants this listener to play where it listens (through the relay): it stops listening but stays.
    private VoiceClient? HandOver(ulong guildId, ulong helperId)
    {
        if (_connections.FirstOrDefault(c => c.Value.GuildId == guildId && c.Value.HelperId == helperId) is not { Value: { } connection }
            || !_connections.TryRemove(connection.ChannelId, out _))
            return null;
        seats.Leave(guildId, helperId);
        Unhear(connection);
        mouths.Unseat(connection.Client);
        return connection.Client;
    }

    // A helper done playing stays to listen where a listener is still wanted.
    private async Task<bool> TakeBackAsync(HelperBot helper, ulong guildId, ulong channelId)
    {
        if (_wanted.GetValueOrDefault(guildId)?.Contains(channelId) != true || _connections.ContainsKey(channelId)
            || await helper.StopPlayingAsync(guildId) is not { } client)
            return false;
        Listen(helper, guildId, channelId, client);
        return true;
    }

    // Music wants this helper: it stops listening wherever it is in that server.
    private async Task ReleaseAsync(ulong guildId, ulong helperId)
    {
        foreach (var (channelId, connection) in _connections.Where(c => c.Value.GuildId == guildId && c.Value.HelperId == helperId).ToList())
            await LeaveAsync(channelId);
    }

    private async Task LeaveAsync(ulong channelId)
    {
        if (!_connections.TryRemove(channelId, out var connection))
            return;
        seats.Leave(connection.GuildId, connection.HelperId);
        Unhear(connection);
        mouths.Forget(connection.Client);
        connection.Client.Dispose();
        try
        {
            await connection.Gateway.UpdateVoiceStateAsync(new VoiceStateProperties(connection.GuildId, null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug("Leaving voice in {GuildId}: {Message}", connection.GuildId, ex.Message);
        }
    }

    // Decodes as it arrives, only for members who let the helpers hear them.
    private void Receive(Connection connection, VoiceReceiveEventArgs args)
    {
        if (!connection.Client.Cache.SsrcUsers.TryGetValue(args.Ssrc, out var userId))
            return;
        // Someone talks (for lowering the music, and talk time): only that audio arrives, nothing of it is decoded.
        if (_bots.GetValueOrDefault(connection.GuildId)?.Contains(userId) != true)
        {
            connection.TalkedAt = time.GetTimestamp();
            if (_talkMode.GetValueOrDefault(connection.GuildId, TalkTimeModes.Off) != TalkTimeModes.Off)
                _talked.AddOrUpdate((connection.GuildId, userId), FrameMs, (_, ms) => ms + FrameMs);
        }
        if (!_on.GetValueOrDefault(connection.GuildId) || !_mayHear.Contains((connection.GuildId, userId)))
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

    // Adds what was talked since the last write to today's totals.
    private async Task SaveTalkTimeAsync()
    {
        _talkFlushedAt = time.GetUtcNow();
        var day = DateOnly.FromDateTime(_talkFlushedAt.UtcDateTime);
        var talked = new List<((ulong Guild, ulong User) Key, long Ms)>();
        foreach (var key in _talked.Keys.ToList())
        {
            if (_talked.TryRemove(key, out var ms))
                talked.Add((key, ms));
        }
        if (talked.Count == 0)
            return;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            foreach (var ((guildId, userId), ms) in talked)
            {
                var row = await db.TalkTimes.FindAsync(guildId, userId, day);
                if (row is null)
                    db.TalkTimes.Add(new() { GuildId = guildId, UserId = userId, Day = day, Ms = ms });
                else
                    row.Ms += ms;
            }
            await db.SaveChangesAsync();
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Saving talk time failed");
        }
    }

    // Whether people talk in each channel a helper hears, every tick: the music there is lowered and brought back.
    private async Task DuckAsync()
    {
        foreach (var connection in _connections.Values.Concat(_borrowed.Values))
        {
            var talking = connection.TalkedAt != 0 && time.GetElapsedTime(connection.TalkedAt).TotalMilliseconds < TalkingMs;
            await work.TalkingAsync(connection.GuildId, connection.ChannelId, talking);
        }
    }

    // Sentences whose speaker paused go off to be turned into text.
    private void FlushPauses()
    {
        foreach (var (channelId, connection) in _connections.Concat(_borrowed))
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
                // Clicks, taps and breathing get through some microphones: only something like speech goes on.
                if (seconds is >= ShortestSeconds and <= LongestSeconds && Speechlike(samples))
                    _sentences.Writer.TryWrite((connection.GuildId, channelId, sentence.UserId, connection.HelperId, To16kHz(samples),
                        trace.Begin(connection.GuildId, channelId, sentence.UserId, samples.Length / (double)Rate)));
            }
        }
    }

    // One at a time: speech to text is the heavy part. What follows the text goes on beside the next sentence's.
    private async Task TranscribeAsync(CancellationToken ct)
    {
        await foreach (var (guildId, channelId, userId, listenerId, audio, sentence) in _sentences.Reader.ReadAllAsync(ct))
        {
            using var scope = logger.BeginScope(DebugLog.Guild(guildId));
            try
            {
                if (sentence is not null && time.GetUtcNow() - sentence.Ended is var waited && waited > TimeSpan.FromMilliseconds(100))
                    trace.Step(sentence, "waited for the sentence before", (int)waited.TotalMilliseconds);
                var started = time.GetTimestamp();
                var text = WithoutNoises(await speech.TranscribeAsync(audio, await NamesAsync(guildId, channelId), ct));
                var took = (int)time.GetElapsedTime(started).TotalMilliseconds;
                // How long each step takes (never what was said), to see where replies are slow.
                logger.LogInformation("Speech to text: {Seconds:0.0} s of speech in {Ms} ms", audio.Length / 16_000.0, took);
                trace.Step(sentence, "speech to text", took, text.Length == 0 ? "(nothing)" : $"“{text}”");
                if (text.Length == 0 || Heard is not { } heard)
                    continue;
                _ = _acting.Run(channelId, () => ActAsync(heard, new(guildId, channelId, userId, listenerId, text, sentence)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Turning a sentence into text failed");
                trace.Step(sentence, "failed", null, ex.Message);
            }
        }
    }

    private async Task ActAsync(Func<Heard, Task> heard, Heard what)
    {
        // Everything logged about the sentence, down to the reply it gets, is this server's.
        using var scope = logger.BeginScope(DebugLog.Guild(what.GuildId));
        try
        {
            foreach (var handler in heard.GetInvocationList().Cast<Func<Heard, Task>>())
                await handler(what);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Acting on a sentence failed");
            trace.Step(what.Trace, "failed", null, ex.Message);
        }
    }

    // The names it should expect to hear: the helpers' in this channel, as the server calls them.
    private async Task<IReadOnlyList<string>> NamesAsync(ulong guildId, ulong channelId)
    {
        var helpers = _connections.Values.Concat(_borrowed.Values).Where(c => c.GuildId == guildId && c.ChannelId == channelId)
            .Select(c => fleet.Helpers.FirstOrDefault(h => h.UserId == c.HelperId)).OfType<HelperBot>();
        var names = new List<string>();
        foreach (var helper in helpers)
            names.Add(await personalities.NameAsync(guildId, helper));
        return [.. names.Distinct()];
    }

    // Loud enough for long enough, most of the time: speech, rather than a key or a click now and then.
    private const int FrameSamples = 960;
    private const double Loud = 500;
    private const double LeastLoudSeconds = 0.35;
    private const double LeastLoudShare = 0.4;

    public static bool Speechlike(short[] samples)
    {
        var frames = samples.Length / FrameSamples;
        if (frames == 0)
            return false;
        var loud = 0;
        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            for (var i = f * FrameSamples; i < (f + 1) * FrameSamples; i++)
                sum += (double)samples[i] * samples[i];
            if (Math.Sqrt(sum / FrameSamples) >= Loud)
                loud++;
        }
        return loud * 0.02 >= LeastLoudSeconds && loud >= frames * LeastLoudShare;
    }

    // Whisper's notes for what isn't words: "[BLANK_AUDIO]", "(keyboard clicking)".
    public static string WithoutNoises(string text) => NoiseNote().Replace(text, "").Trim();

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*")]
    private static partial Regex NoiseNote();

    // 48 kHz to 16 kHz, as Whisper wants it: what's above 7 kHz filtered out first (left in, the hiss of s, f, th
    // and j folds down into the speech and blurs them), then every third sample, brought to an even level.
    public static float[] To16kHz(short[] samples)
    {
        var result = new float[samples.Length / 3];
        var half = LowPass.Length / 2;
        for (var i = 0; i < result.Length; i++)
        {
            var at = 3 * i;
            double sum = 0;
            for (var k = 0; k < LowPass.Length; k++)
            {
                var j = at + k - half;
                if (j >= 0 && j < samples.Length)
                    sum += samples[j] * LowPass[k];
            }
            result[i] = (float)(sum / 32768);
        }
        var peak = result.Length == 0 ? 0 : result.Max(Math.Abs);
        if (peak is > 0 and < Peak)
        {
            var gain = Math.Min(Peak / peak, MostGain);
            for (var i = 0; i < result.Length; i++)
                result[i] *= gain;
        }
        return result;
    }

    // Quiet microphones brought up to this peak, by at most this much.
    private const float Peak = 0.9f;
    private const float MostGain = 8f;

    // A windowed-sinc low-pass at 7 kHz for 48 kHz audio (Blackman window), its taps summing to 1.
    private static readonly double[] LowPass = MakeLowPass(63, 7000.0 / 48000);

    private static double[] MakeLowPass(int taps, double cutoff)
    {
        var filter = new double[taps];
        var middle = (taps - 1) / 2.0;
        for (var n = 0; n < taps; n++)
        {
            var x = n - middle;
            var sinc = x == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
            var window = 0.42 - 0.5 * Math.Cos(2 * Math.PI * n / (taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * n / (taps - 1));
            filter[n] = sinc * window;
        }
        var total = filter.Sum();
        return [.. filter.Select(f => f / total)];
    }

    private sealed class Connection(GatewayClient gateway, VoiceClient client, ulong guildId, ulong channelId, ulong helperId)
    {
        public GatewayClient Gateway { get; } = gateway;

        public VoiceClient Client { get; } = client;

        public ulong GuildId { get; } = guildId;

        public ulong ChannelId { get; } = channelId;

        public ulong HelperId { get; } = helperId;

        public Func<VoiceReceiveEventArgs, ValueTask>? Receiver { get; set; }

        // When anyone (not a bot) last talked there.
        public long TalkedAt { get; set; }

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
