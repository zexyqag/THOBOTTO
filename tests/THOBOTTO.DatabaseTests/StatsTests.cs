using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

using NodaTime;

using THOBOTTO.Integrations;
using THOBOTTO.Lastfm;
using THOBOTTO.Quiz;
using THOBOTTO.Stats;

namespace THOBOTTO.DatabaseTests;

// The queries behind Wrapped and the music quiz, run on Postgres (some only fail there, in translation).
[Collection(DatabaseCollection.Name)]
public class StatsTests(TestDatabase database)
{
    private static readonly DateTimeOffset October = new(2026, 10, 5, 20, 0, 0, TimeSpan.Zero);
    private static readonly WrappedSpan All = new("all time", DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
    private const ulong Ana = 11, Bo = 12, Helper = 99;

    private WrappedStats Stats()
    {
        var store = new IntegrationStore(database, new EphemeralDataProtectionProvider(), TimeProvider.System, NullLogger<IntegrationStore>.Instance);
        return new(database, new GenreBook(new LastfmClient(store), database, TimeProvider.System, NullLogger<GenreBook>.Instance));
    }

    private async Task SeedAsync(ulong guildId)
    {
        await using var db = database.CreateDbContext();
        PlayRecord Play(string artist, string title, bool skipped = false, string? personality = null) => new()
        {
            GuildId = guildId,
            VoiceChannelId = 1,
            HelperId = Helper,
            Personality = personality,
            Artist = artist,
            Title = title,
            Source = "youtube",
            Uri = $"https://youtu.be/{title.Replace(' ', '-')}",
            LengthMs = 200_000,
            PlayedMs = skipped ? 10_000 : 200_000,
            Skipped = skipped,
            RequestedBy = Ana,
            StartedAt = October,
        };
        var plays = new[]
        {
            Play("ABBA", "Dancing Queen"), Play("ABBA", "Dancing Queen", personality: "Jeeves"), Play("ABBA", "Waterloo"),
            Play("AC/DC", "Thunderstruck"), Play("AC/DC", "Thunderstruck", skipped: true),
        };
        db.PlayRecords.AddRange(plays);
        await db.SaveChangesAsync();
        db.PlayListeners.AddRange(plays.Select(p => new PlayListener { PlayId = p.Id, UserId = Ana }).Append(new() { PlayId = plays[0].Id, UserId = Bo }));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_servers_music_is_counted()
    {
        const ulong guild = 1001;
        await SeedAsync(guild);
        var music = await Stats().MusicAsync(guild, new(), All, DateTimeZone.Utc, id => $"helper {id}");
        Assert.Equal(4, music.Plays);
        Assert.Equal(new Ranked("Dancing Queen · ABBA", 2), music.TopSongs[0]);
        Assert.Equal("ABBA", music.TopArtists[0].Name);
        Assert.Equal(0.2, music.SkipShare, 3);
        Assert.Equal(Ana, music.Requesters[0].User);
    }

    [Fact]
    public async Task A_members_music_is_what_they_heard()
    {
        const ulong guild = 1002;
        await SeedAsync(guild);
        var music = await Stats().MusicAsync(guild, new(UserId: Bo), All, DateTimeZone.Utc, id => $"helper {id}");
        Assert.Equal(1, music.Plays);
        Assert.Equal(Ana, music.Companions[0].User);
    }

    [Fact]
    public async Task Voices_are_ranked_by_plays()
    {
        const ulong guild = 1003;
        await SeedAsync(guild);
        var voices = await Stats().VoicesAsync(guild);
        Assert.Equal((null, Helper, 4), voices[0]);
        Assert.Equal(("Jeeves", 0UL, 1), voices[1]);
    }

    [Fact]
    public async Task Discord_activity_reads_on_an_empty_server()
    {
        var summary = await new ActivityStats(database).ForAsync(1004, null, All, DateTimeZone.Utc, new HashSet<ulong>());
        Assert.True(summary.Empty);
    }

    [Fact]
    public async Task The_quiz_takes_the_songs_played_most_once_each()
    {
        const ulong guild = 1005;
        await SeedAsync(guild);
        await using var db = database.CreateDbContext();
        var songs = await QuizRooms.PlayedMostAsync(db, guild);
        Assert.Equal(["Dancing Queen", "Thunderstruck", "Waterloo"], songs.Select(s => s.Title).Order());
        Assert.Equal("Waterloo", songs[^1].Title);
    }
}
