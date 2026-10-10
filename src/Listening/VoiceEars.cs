using System.Collections.Concurrent;
using System.Threading.Channels;

using Microsoft.EntityFrameworkCore;

using NetCord.Gateway;
using NetCord.Gateway.Voice;

using THOBOTTO.Data;
using THOBOTTO.Helpers;
using THOBOTTO.Modules;
using THOBOTTO.Music;
using THOBOTTO.Voice;
using THOBOTTO.Speaking;

namespace THOBOTTO.Listening;

// Something a member said, as text, and which helper heard it.
public sealed record Heard(ulong GuildId, ulong ChannelId, ulong UserId, ulong ListenerId, string Text, TraceSentence? Trace = null);

// Helpers' ears. A free helper (one not playing in that server) sits muted in a voice channel when someone
// there summoned it (/listen) or has it join them automatically, and turns what members who let it hear them
// say into text, a sentence at a time (Discord only sends audio while someone talks, so a pause ends one).
// Everyone else's audio is dropped undecoded; audio and text are never stored. When helpers run out, the
// server's priority decides between music and listening.
public sealed class VoiceEars(
    GatewayClient gateway,
    HelperFleet fleet,
    MusicService music,
    ListeningSeats seats,
    VoicePrefs prefs,
    ModuleState modules,
    SettingsStore settings,
    VoicePresence presence,
    ISpeechToText speech,
    PersonalityBook personalities,
    IDbContextFactory<BotDbContext> dbFactory,
    VoiceMouths mouths,
    THOBOTTO.Lastfm.LastfmClient lastfm,
    VoiceTrace trace,
    TimeProvider time,
    ILogger<VoiceEars> logger) : BackgroundService, IHelperAware
{
    public const string ModuleId = "listening";

    private static readonly TimeSpan Check = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(200);
    // A summoned helper stays this long after the last member who lets it hear them left.
    private static readonly TimeSpan SummonGrace = TimeSpan.FromMinutes(1);
    // This long without audio from someone ends their sentence.
    private const long PauseMs = 700;
    private const double ShortestSeconds = 0.4;
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
    // Servers with the listening module on.
    private readonly ConcurrentDictionary<ulong, bool> _on = new();
    // Server → (lower the music while people talk, to what percent).
    private readonly ConcurrentDictionary<ulong, (bool On, int Percent)> _ducking = new();
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

    // /listen: a helper comes to the member's channel. Returns the answer.
    public async Task<string> SummonAsync(ulong guildId, ulong userId)
    {
        if (!(await prefs.GetAsync(guildId, userId)).Listen)
            return "First let the helpers hear you: `/me voice-commands on`.";
        if (!presence.Snapshot(guildId).TryGetValue(userId, out var where))
            return "Join a voice channel first.";
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
            FlushPauses();
            await DuckAsync();
            if (time.GetUtcNow() - _talkFlushedAt >= TalkFlush)
                await SaveTalkTimeAsync();
            if (time.GetUtcNow() < nextCheck)
                continue;
            nextCheck = time.GetUtcNow() + Check;
            try
            {
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
        await SaveTalkTimeAsync();
        foreach (var channelId in _connections.Keys.ToList())
            await LeaveAsync(channelId);
    }

    private async Task ReconcileAsync(ulong guildId)
    {
        var all = await prefs.AllAsync();
        _mayHear = all.Values.Where(p => p.Listen).Select(p => (p.GuildId, p.UserId)).ToHashSet();
        var on = _on[guildId] = await modules.IsEnabledAsync(guildId, ModuleId);
        var musicRules = await settings.GetAsync<MusicRules>(guildId, MusicService.ModuleId);
        _ducking[guildId] = (musicRules.DuckWhileTalking, musicRules.DuckPercent);
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
        // Counting talk time everywhere: a helper sits in each voice channel with people (not the AFK one).
        if (rules.TalkTime == TalkTimeModes.Everywhere)
        {
            var afk = gateway.Cache.Guilds.TryGetValue(guildId, out var cached) ? cached.AfkChannelId : null;
            wanted.UnionWith(present.Select(p => p.Value.ChannelId).Where(c => c != afk));
        }
        if (rules.AutoListenAllowed)
        {
            foreach (var (userId, where) in present)
            {
                if (all.GetValueOrDefault((guildId, userId)) is { Listen: true, AutoListen: true })
                    wanted.Add(where.ChannelId);
            }
        }
        if (!on)
            wanted.Clear();
        _wanted[guildId] = wanted;

        foreach (var (channelId, connection) in _connections.Where(c => c.Value.GuildId == guildId).ToList())
        {
            // A helper sent to play since doesn't listen any more; where the player hears, no listener is needed.
            if (!wanted.Contains(channelId) || _borrowed.ContainsKey(channelId) || fleet.Helpers.FirstOrDefault(h => h.UserId == connection.HelperId)?.Players.ContainsKey(guildId) == true)
                await LeaveAsync(channelId);
        }
        foreach (var channelId in wanted.Where(c => !_connections.ContainsKey(c) && !_borrowed.ContainsKey(c)))
        {
            if (await HelperToListenAsync(guildId, channelId, rules) is { } helper)
                await JoinAsync(helper, guildId, channelId);
        }
    }

    // A free helper; else, when listening comes first, one taken off music somewhere else.
    private async Task<HelperBot?> HelperToListenAsync(ulong guildId, ulong channelId, ListeningRules rules)
    {
        var here = fleet.Helpers.Where(h => h.InGuild(guildId)).ToList();
        if (here.FirstOrDefault(h => !h.Players.ContainsKey(guildId) && !seats.IsListening(guildId, h.UserId)) is { } free)
            return free;
        if (rules.Priority != HelperPriorities.ListeningFirst)
            return null;
        if (music.PlayersIn(guildId).FirstOrDefault(p => !p.Plays(channelId)) is not { } player)
            return null;
        await player.StopAsync();
        await music.DisconnectAsync(player, stayToListen: false);
        return player.Helper;
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
            logger.LogWarning("Joining {ChannelId} to listen failed: {Message}", channelId, ex.Message);
        }
    }

    private void Listen(HelperBot listener, ulong guildId, ulong channelId, VoiceClient client)
    {
        _connections[channelId] = Hear(new(listener.Gateway, client, guildId, channelId, listener.UserId));
        seats.Sit(guildId, listener.UserId, channelId);
        // A helper playing there through the relay speaks there; else this one does.
        if (!_borrowed.ContainsKey(channelId))
            mouths.Seat(guildId, channelId, new(listener, client, Muted: true));
        logger.LogInformation("{Helper} listening in {ChannelId}", listener.Name, channelId);
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

    // Lowers the music in channels where people talk (and brings it back), where the server wants that.
    private async Task DuckAsync()
    {
        foreach (var connection in _connections.Values.Concat(_borrowed.Values))
        {
            if (music.PlayerIn(connection.GuildId, connection.ChannelId) is not { } player)
                continue;
            var playing = player.Mirrors.FirstOrDefault(m => m.VoiceChannelId == connection.ChannelId)?.Helper ?? player.Helper;
            var (on, percent) = _ducking.GetValueOrDefault(connection.GuildId);
            var talking = on && connection.TalkedAt != 0 && time.GetElapsedTime(connection.TalkedAt).TotalMilliseconds < TalkingMs;
            var target = talking ? percent : 100;
            if (player.LevelOf(playing) == target)
                continue;
            try
            {
                // A step a tick: it fades rather than jumps.
                await player.FadeAsync(playing, target);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogDebug("Lowering the music: {Message}", ex.Message);
            }
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
                if (seconds is >= ShortestSeconds and <= LongestSeconds)
                    _sentences.Writer.TryWrite((connection.GuildId, channelId, sentence.UserId, connection.HelperId, To16kHz(samples),
                        trace.Begin(connection.GuildId, channelId, sentence.UserId, samples.Length / (double)Rate)));
            }
        }
    }

    // One at a time: speech to text is the heavy part.
    private async Task TranscribeAsync(CancellationToken ct)
    {
        await foreach (var (guildId, channelId, userId, listenerId, audio, sentence) in _sentences.Reader.ReadAllAsync(ct))
        {
            try
            {
                if (sentence is not null && time.GetUtcNow() - sentence.Ended is var waited && waited > TimeSpan.FromMilliseconds(100))
                    trace.Step(sentence, "waited for the sentence before", (int)waited.TotalMilliseconds);
                var started = time.GetTimestamp();
                var text = await speech.TranscribeAsync(audio, await HintAsync(guildId, channelId), ct);
                var took = (int)time.GetElapsedTime(started).TotalMilliseconds;
                // How long each step takes (never what was said), to see where replies are slow.
                logger.LogInformation("Speech to text: {Seconds:0.0} s of speech in {Ms} ms", audio.Length / 16_000.0, took);
                trace.Step(sentence, "speech to text", took, text.Length == 0 ? "(nothing)" : $"“{text}”");
                if (text.Length == 0 || Heard is not { } heard)
                    continue;
                foreach (var handler in heard.GetInvocationList().Cast<Func<Heard, Task>>())
                    await handler(new(guildId, channelId, userId, listenerId, text, sentence));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Understanding a sentence failed");
                trace.Step(sentence, "failed", null, ex.Message);
            }
        }
    }

    // The names it should expect to hear: the helpers', as the server calls them, and artists: those the people in
    // the call listen to most (on Last.fm, if they linked it), then those played most in the server (Whisper spells
    // band names freely otherwise: "Aba" for ABBA).
    private async Task<string> HintAsync(ulong guildId, ulong channelId)
    {
        var names = (await personalities.ListAsync(guildId)).Select(p => p.Name).Concat(fleet.Helpers.Select(h => h.Name)).Distinct();
        var inCall = presence.Snapshot(guildId).Where(p => p.Value.ChannelId == channelId && !p.Value.IsBot).Select(p => p.Key).ToList();
        var artists = (await ListenedAsync(inCall)).Concat(await ArtistsAsync(guildId))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(HintArtists).ToList();
        return $"{string.Join(", ", names)}. Play, queue, add, next, skip, pause, resume, stop, louder, quieter, what's playing."
            + (artists.Count > 0 ? $" {string.Join(", ", artists)}." : "");
    }

    private const int HintArtists = 30;
    private static readonly TimeSpan ArtistsFresh = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<ulong, (IReadOnlyList<string> Artists, DateTimeOffset At)> _artists = new();

    private async Task<IReadOnlyList<string>> ArtistsAsync(ulong guildId)
    {
        if (_artists.TryGetValue(guildId, out var known) && time.GetUtcNow() - known.At < ArtistsFresh)
            return known.Artists;
        await using var db = await dbFactory.CreateDbContextAsync();
        var since = time.GetUtcNow() - TimeSpan.FromDays(180);
        var played = await db.PlayRecords.AsNoTracking().Where(p => p.GuildId == guildId && p.StartedAt >= since)
            .GroupBy(p => p.Artist).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(HintArtists * 2).ToListAsync();
        var artists = played.Select(CleanArtist).Where(a => a.Length is > 1 and < 40).Distinct(StringComparer.OrdinalIgnoreCase).Take(HintArtists).ToList();
        _artists[guildId] = (artists, time.GetUtcNow());
        return artists;
    }

    private const int ListenedPerPerson = 15;
    private static readonly TimeSpan ListenedFresh = TimeSpan.FromHours(12);
    private readonly ConcurrentDictionary<ulong, (IReadOnlyList<string> Artists, DateTimeOffset At)> _listened = new();

    // Each linked member's most-played artists on Last.fm lately.
    private async Task<IReadOnlyList<string>> ListenedAsync(IReadOnlyList<ulong> userIds)
    {
        if (!lastfm.Configured || userIds.Count == 0)
            return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var links = await db.LastfmLinks.AsNoTracking().Where(l => userIds.Contains(l.UserId)).ToListAsync();
        var artists = new List<string>();
        foreach (var link in links)
        {
            if (!_listened.TryGetValue(link.UserId, out var known) || time.GetUtcNow() - known.At >= ListenedFresh)
            {
                try
                {
                    var top = (await lastfm.TopTracksAsync(link.Username)).Select(s => s.Artist).Distinct(StringComparer.OrdinalIgnoreCase).Take(ListenedPerPerson).ToList();
                    _listened[link.UserId] = known = (top, time.GetUtcNow());
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
                {
                    logger.LogDebug("Last.fm artists for {UserId}: {Message}", link.UserId, ex.Message);
                    continue;
                }
            }
            artists.AddRange(known.Artists);
        }
        return artists;
    }

    // "ABBA - Topic" and "ABBAVEVO", as YouTube channels name artists, to "ABBA".
    public static string CleanArtist(string artist)
    {
        var name = artist.Trim();
        if (name.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase))
            name = name[..^8];
        if (name.EndsWith("VEVO", StringComparison.Ordinal) && name.Length > 4)
            name = name[..^4];
        return name.Trim();
    }

    // 48 kHz to 16 kHz (averaging each three samples), as Whisper wants it.
    public static float[] To16kHz(short[] samples)
    {
        var result = new float[samples.Length / 3];
        for (var i = 0; i < result.Length; i++)
            result[i] = (samples[3 * i] + samples[3 * i + 1] + samples[3 * i + 2]) / (3 * 32768f);
        return result;
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
