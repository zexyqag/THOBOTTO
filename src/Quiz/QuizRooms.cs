using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Lastfm;
using THOBOTTO.Modules;
using THOBOTTO.Music;
using THOBOTTO.Points;
using THOBOTTO.Voice;

namespace THOBOTTO.Quiz;

public sealed record QuizSong(string Artist, string Title, string? Uri, Track? Track)
{
    public string Label => $"{Title} — {Artist}";
}

public sealed record QuizRound(QuizSong Song, Track Clip, long From, IReadOnlyList<string> Choices, int Correct);

public enum QuizPhase
{
    Starting,
    Asking,
    Reveal,
    Over,
}

// A music quiz in a voice channel, played in its Activity.
public sealed class QuizRoom(ulong guildId, ulong channelId, ulong startedBy, MusicPlayer player, List<QuizSong> songs, int rounds)
{
    public ulong GuildId => guildId;

    public ulong ChannelId => channelId;

    public ulong StartedBy => startedBy;

    public MusicPlayer Player => player;

    // Shuffled; more than the rounds, as some won't load.
    public List<QuizSong> Songs => songs;

    public int Rounds => rounds;

    public int Number { get; set; }

    public QuizPhase Phase { get; set; }

    public DateTimeOffset PhaseAt { get; set; }

    public DateTimeOffset EndsAt { get; set; }

    public QuizRound? Round { get; set; }

    public Task<QuizRound?>? Next { get; set; }

    // This round's answers: who → (choice, when).
    public Dictionary<ulong, (int Choice, DateTimeOffset At)> Answers { get; } = [];

    // This round's points, once revealed.
    public Dictionary<ulong, int> Gains { get; } = [];

    public Dictionary<ulong, int> Scores { get; } = [];

    public SemaphoreSlim Gate { get; } = new(1, 1);
}

public sealed record QuizAnswer(ulong UserId, int Choice, int Gain);

public sealed record QuizState(
    QuizPhase Phase, int Number, int Rounds, long LeftMs, IReadOnlyList<string> Choices, int? Mine, int Answered, int Players,
    int? Correct, QuizSong? Song, string? Art, IReadOnlyList<QuizAnswer> Picks, IReadOnlyList<(ulong UserId, int Score)> Scores);

// Music quizzes, one per voice channel: a helper plays a clip of a song (the music there waits), everyone in the
// channel picks its name from four, quicker right answers score more. Songs come from what's played most here,
// or a playlist set on the panel.
public sealed class QuizRooms(
    MusicService music,
    SettingsStore settings,
    ModuleState modules,
    VoicePresence presence,
    PointsEngine points,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time,
    ILogger<QuizRooms> logger) : BackgroundService
{
    public const string ModuleId = "quiz";
    private static readonly TimeSpan Countdown = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Revealed = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan Standings = TimeSpan.FromSeconds(30);
    private const int Choices = 4;
    private const int MostSongs = 200;
    private const long ShortestSong = 60_000;

    private readonly ConcurrentDictionary<(ulong, ulong), QuizRoom> _rooms = new();

    public async Task<string> StartAsync(ulong guildId, ulong channelId, ulong userId)
    {
        if (!await modules.IsEnabledAsync(guildId, ModuleId))
            return $"The `{ModuleId}` module is off.";
        if (!await modules.IsEnabledAsync(guildId, MusicService.ModuleId))
            return $"The quiz needs the `{MusicService.ModuleId}` module on.";
        if (_rooms.ContainsKey((guildId, channelId)))
            return "A quiz is already on here.";
        var rules = await settings.GetAsync<QuizRules>(guildId, ModuleId);
        var songs = await SongsAsync(guildId, rules);
        if (songs.Count < Choices * 2)
            return string.IsNullOrWhiteSpace(rules.Playlist)
                ? "Too few songs were played here yet for a quiz. Play more, or set a playlist for it on the panel."
                : "The quiz's playlist has too few songs, or couldn't be loaded.";
        var (player, problem) = await music.PlayerForAsync(guildId, channelId, channelId);
        if (player is null)
            return problem!;
        if (player.Quizzing)
            return "A quiz is already on with that helper.";

        var room = new QuizRoom(guildId, channelId, userId, player, [.. songs.OrderBy(_ => Random.Shared.Next())], Math.Min(rules.Rounds, songs.Count));
        if (!_rooms.TryAdd((guildId, channelId), room))
            return "A quiz is already on here.";
        room.Next = PrepareAsync(room);
        (room.Phase, room.PhaseAt, room.EndsAt) = (QuizPhase.Starting, time.GetUtcNow(), time.GetUtcNow() + Countdown);
        await music.PostAsync(guildId, player.Helper, channelId, new()
        {
            Content = $"🎯 <@{userId}> started a music quiz: {room.Rounds} songs. Open the Activity in the voice channel to play.",
            AllowedMentions = AllowedMentionsProperties.None,
        });
        return "🎯 The quiz starts in a few seconds.";
    }

    // Null when taken.
    public async Task<string?> AnswerAsync(ulong guildId, ulong channelId, ulong userId, int choice)
    {
        if (!_rooms.TryGetValue((guildId, channelId), out var room))
            return "No quiz is on here.";
        await room.Gate.WaitAsync();
        try
        {
            if (room.Phase != QuizPhase.Asking || choice < 0 || choice >= Choices)
                return "Not now.";
            if (!room.Answers.TryAdd(userId, (choice, time.GetUtcNow())))
                return "You already answered.";
            return null;
        }
        finally
        {
            room.Gate.Release();
        }
    }

    // The one who started it, or someone who may control the music.
    public async Task<string> StopAsync(ulong guildId, ulong channelId, GuildUser member)
    {
        if (!_rooms.TryGetValue((guildId, channelId), out var room))
            return "No quiz is on here.";
        if (!await CanStopAsync(guildId, channelId, member))
            return "Only who started the quiz, or who may control the music, stops it.";
        await EndAsync(room);
        return "The quiz stopped.";
    }

    public async Task<bool> CanStopAsync(ulong guildId, ulong channelId, GuildUser member)
        => _rooms.TryGetValue((guildId, channelId), out var room)
            && (room.StartedBy == member.Id || await music.RefusalAsync(guildId, member, room.Player) is null);

    public QuizState? State(ulong guildId, ulong channelId, ulong userId)
    {
        if (!_rooms.TryGetValue((guildId, channelId), out var room))
            return null;
        var now = time.GetUtcNow();
        var shown = room.Phase is QuizPhase.Reveal or QuizPhase.Over;
        var round = room.Round;
        return new(
            room.Phase, room.Number, room.Rounds, (long)Math.Max(0, (room.EndsAt - now).TotalMilliseconds),
            room.Phase == QuizPhase.Starting || round is null ? [] : round.Choices,
            room.Answers.TryGetValue(userId, out var mine) ? mine.Choice : null,
            room.Answers.Count, Players(room).Count,
            shown ? round?.Correct : null, shown ? round?.Song : null, shown ? round?.Clip.Artwork : null,
            shown ? [.. room.Answers.Select(a => new QuizAnswer(a.Key, a.Value.Choice, room.Gains.GetValueOrDefault(a.Key)))] : [],
            [.. room.Scores.OrderByDescending(s => s.Value).Select(s => (s.Key, s.Value))]);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            foreach (var room in _rooms.Values)
                try
                {
                    await StepAsync(room);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Music quiz in {ChannelId} failed", room.ChannelId);
                    await EndAsync(room);
                }
    }

    private async Task StepAsync(QuizRoom room)
    {
        var now = time.GetUtcNow();
        // The helper went (thrown out, stopped), or everyone left.
        if (room.Player.Helper.Players.GetValueOrDefault(room.GuildId) != room.Player || Players(room).Count == 0)
        {
            await EndAsync(room);
            return;
        }
        await room.Gate.WaitAsync();
        try
        {
            // Stopped meanwhile.
            if (_rooms.GetValueOrDefault((room.GuildId, room.ChannelId)) != room)
                return;
            switch (room.Phase)
            {
                case QuizPhase.Starting or QuizPhase.Reveal when now >= room.EndsAt:
                    if (room.Number >= room.Rounds || await room.Next! is not { } round)
                    {
                        await FinishAsync(room);
                        return;
                    }
                    room.Number++;
                    room.Round = round;
                    room.Answers.Clear();
                    room.Gains.Clear();
                    var rules = await settings.GetAsync<QuizRules>(room.GuildId, ModuleId);
                    await room.Player.ClipAsync(round.Clip, round.From);
                    (room.Phase, room.PhaseAt, room.EndsAt) = (QuizPhase.Asking, now, now + TimeSpan.FromSeconds(rules.ClipSeconds));
                    room.Next = room.Number < room.Rounds ? PrepareAsync(room) : Task.FromResult<QuizRound?>(null);
                    break;
                case QuizPhase.Asking when now >= room.EndsAt || Players(room).All(room.Answers.ContainsKey):
                    var asked = room.EndsAt - room.PhaseAt;
                    foreach (var (userId, (choice, at)) in room.Answers.Where(a => a.Value.Choice == room.Round!.Correct))
                    {
                        var gain = Score(at - room.PhaseAt, asked);
                        room.Gains[userId] = gain;
                        room.Scores[userId] = room.Scores.GetValueOrDefault(userId) + gain;
                    }
                    foreach (var userId in room.Answers.Keys)
                        room.Scores.TryAdd(userId, 0);
                    (room.Phase, room.PhaseAt, room.EndsAt) = (QuizPhase.Reveal, now, now + Revealed);
                    break;
                case QuizPhase.Over when now >= room.EndsAt:
                    _rooms.TryRemove((room.GuildId, room.ChannelId), out _);
                    break;
            }
        }
        finally
        {
            room.Gate.Release();
        }
    }

    // 500 for a right answer at the end, up to 1000 right away, in tens.
    public static int Score(TimeSpan took, TimeSpan asked)
        => (int)Math.Round(50 + 50 * Math.Clamp(1 - took / asked, 0, 1)) * 10;

    private async Task FinishAsync(QuizRoom room)
    {
        (room.Phase, room.PhaseAt, room.EndsAt) = (QuizPhase.Over, time.GetUtcNow(), time.GetUtcNow() + Standings);
        await room.Player.ClipAsync(null, 0);
        await room.Player.EndQuizAsync();
        if (room.Scores.Count == 0)
            return;
        var standings = room.Scores.OrderByDescending(s => s.Value).ToList();
        var best = standings[0].Value;
        var winners = standings.Where(s => s.Value == best && best > 0).Select(s => s.Key).ToList();
        var rules = await settings.GetAsync<QuizRules>(room.GuildId, ModuleId);
        if (rules.WinnerPoints > 0 && winners.Count > 0 && await modules.IsEnabledAsync(room.GuildId, PointsEngine.ModuleId))
            foreach (var winner in winners)
                await points.AwardAsync(room.GuildId, winner, (double)rules.WinnerPoints / winners.Count, PointEntryKinds.Quiz, "music quiz winner");
        var medals = new[] { "🥇", "🥈", "🥉" };
        await music.PostAsync(room.GuildId, room.Player.Helper, room.ChannelId, new()
        {
            Content = best == 0 ? "🎯 **Music quiz over:** nobody named a song this time."
                : $"🎯 **Music quiz results**\n{string.Join('\n', standings.Take(10).Select((s, i) => $"{(i < medals.Length && s.Value > 0 ? medals[i] : $"{i + 1}.")} <@{s.Key}> · {s.Value}"))}",
            AllowedMentions = AllowedMentionsProperties.None,
        });
    }

    private async Task EndAsync(QuizRoom room)
    {
        if (!_rooms.TryRemove((room.GuildId, room.ChannelId), out _))
            return;
        await room.Gate.WaitAsync();
        try
        {
            if (room.Phase == QuizPhase.Over)
                return;
            await room.Player.ClipAsync(null, 0);
            await room.Player.EndQuizAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            logger.LogDebug("Ending the quiz in {ChannelId}: {Message}", room.ChannelId, ex.Message);
        }
        finally
        {
            room.Gate.Release();
        }
    }

    private List<ulong> Players(QuizRoom room)
        => [.. presence.Snapshot(room.GuildId).Where(p => p.Value.ChannelId == room.ChannelId && !p.Value.IsBot).Select(p => p.Key)];

    // The next song that loads, with three others to pick from (other artists where there are enough).
    private async Task<QuizRound?> PrepareAsync(QuizRoom room)
    {
        var rules = await settings.GetAsync<QuizRules>(room.GuildId, ModuleId);
        while (true)
        {
            QuizSong song;
            lock (room.Songs)
            {
                if (room.Songs.Count == 0)
                    return null;
                song = room.Songs[0];
                room.Songs.RemoveAt(0);
            }
            var clip = song.Track ?? await LoadAsync(song.Uri!, room.Player.Helper.UserId);
            if (clip is null || clip.IsStream || clip.LengthMs < ShortestSong)
                continue;
            var others = room.Songs.Where(s => !s.Title.Equals(song.Title, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.Artist.Equals(song.Artist, StringComparison.OrdinalIgnoreCase)).ThenBy(_ => Random.Shared.Next())
                .Select(s => s.Label).Distinct().Take(Choices - 1).ToList();
            if (others.Count < Choices - 1)
                return null;
            var correct = Random.Shared.Next(Choices);
            others.Insert(correct, song.Label);
            var latest = clip.LengthMs - rules.ClipSeconds * 1000L - 5_000;
            var from = (long)(clip.LengthMs * (0.25 + Random.Shared.NextDouble() * 0.3));
            return new(song, clip, Math.Clamp(from, 0, Math.Max(0, latest)), others, correct);
        }
    }

    private async Task<Track?> LoadAsync(string uri, ulong userId)
    {
        try
        {
            return (await LavalinkConnection.LoadAsync(music.Lavalink, uri, userId)).Tracks.FirstOrDefault();
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task<List<QuizSong>> SongsAsync(ulong guildId, QuizRules rules)
    {
        if (!string.IsNullOrWhiteSpace(rules.Playlist))
        {
            try
            {
                var loaded = await LavalinkConnection.LoadAsync(music.Lavalink, rules.Playlist.Trim(), 0);
                return [.. loaded.Tracks.Where(t => !t.IsStream && t.LengthMs >= ShortestSong)
                    .Select(t =>
                    {
                        var (artist, title) = TrackNames.Tidy(t);
                        return new QuizSong(artist, title, t.Uri, t);
                    })
                    .DistinctBy(s => s.Title.ToLowerInvariant()).Take(MostSongs)];
            }
            catch (HttpRequestException)
            {
                return [];
            }
        }
        await using var db = await dbFactory.CreateDbContextAsync();
        var played = await db.PlayRecords.AsNoTracking()
            .Where(p => p.GuildId == guildId && p.Uri != null && p.LengthMs >= ShortestSong && p.Title != "" && p.Artist != "")
            .GroupBy(p => new { p.Artist, p.Title })
            .Select(g => new { g.Key.Artist, g.Key.Title, Uri = g.Max(p => p.Uri), Plays = g.Count() })
            .OrderByDescending(g => g.Plays).Take(MostSongs * 2)
            .ToListAsync();
        return [.. played.Select(p => new QuizSong(p.Artist, p.Title, p.Uri, null)).DistinctBy(s => s.Title.ToLowerInvariant()).Take(MostSongs)];
    }
}
