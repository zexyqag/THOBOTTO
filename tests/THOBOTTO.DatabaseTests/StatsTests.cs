using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

using NodaTime;

using THOBOTTO.Archive;
using THOBOTTO.Integrations;
using THOBOTTO.Music;
using THOBOTTO.Music.Quiz;
using THOBOTTO.Wrapped;

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
        return new(database, new GenreBook(new LastfmClient(store, new HttpClient()), database, TimeProvider.System, NullLogger<GenreBook>.Instance));
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
        var summary = await new DiscordStats(database).ForAsync(1004, null, All, DateTimeZone.Utc, new HashSet<ulong>());
        Assert.True(summary.Empty);
    }

    // Counted in the server's zone, where an hour needn't be a UTC hour; bots' messages left out, by Discord's mark or by id.
    [Fact]
    public async Task Discord_messages_are_counted_where_they_are()
    {
        const ulong guild = 1006, channel = 5, other = 6, marked = 98;
        var kathmandu = DateTimeZoneProviders.Tzdb["Asia/Kathmandu"];
        await using (var db = database.CreateDbContext())
        {
            // Monday 20:00 UTC is Tuesday 01:45 in Kathmandu (+05:45).
            db.ArchivedMessages.AddRange(
                new() { Id = 1, GuildId = guild, ChannelId = channel, AuthorId = Ana, CreatedAt = October, Content = "gg <:pog:123> 🎉" },
                new() { Id = 2, GuildId = guild, ChannelId = channel, AuthorId = Ana, CreatedAt = October.AddMinutes(10), Content = "🎉🎉 words" },
                new() { Id = 3, GuildId = guild, ChannelId = other, AuthorId = Bo, CreatedAt = October.AddHours(5), Content = "no emoji" },
                new() { Id = 4, GuildId = guild, ChannelId = channel, AuthorId = marked, CreatedAt = October, Content = "🎉", Raw = """{"author":{"id":"98","bot":true}}""" },
                new() { Id = 5, GuildId = guild, ChannelId = channel, AuthorId = Helper, CreatedAt = October, Content = "🎉" });
            await db.SaveChangesAsync();
        }
        var summary = await new DiscordStats(database).ForAsync(guild, null, All, kathmandu, new HashSet<ulong> { Helper });
        Assert.Equal(3, summary.Messages);
        Assert.Equal([(Ana, 2), (Bo, 1)], summary.Talkers);
        Assert.Equal([(channel, 2), (other, 1)], summary.Channels);
        Assert.Equal([("🎉", 2), ("<:pog:123>", 1)], summary.Emoji);
        Assert.Equal(1, summary.BusiestHour);
        Assert.Equal(IsoDayOfWeek.Tuesday, summary.BusiestDay);
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
