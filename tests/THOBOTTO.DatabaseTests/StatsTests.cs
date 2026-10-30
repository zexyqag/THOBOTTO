using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using NodaTime;

using THOBOTTO.Events;
using THOBOTTO.Fame;
using THOBOTTO.Integrations;
using THOBOTTO.Modules;
using THOBOTTO.Music;
using THOBOTTO.Music.Quiz;
using THOBOTTO.Points;
using THOBOTTO.Quotes;
using THOBOTTO.Wrapped;

namespace THOBOTTO.DatabaseTests;

// The queries behind Wrapped and the music quiz, run on Postgres (some only fail there, in translation).
[Collection(DatabaseCollection.Name)]
public class StatsTests(TestDatabase database)
{
    private static readonly DateTimeOffset October = new(2026, 10, 5, 20, 0, 0, TimeSpan.Zero);
    private static readonly WrappedSpan All = new("all time", DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
    private const ulong Ana = 11, Bo = 12, Helper = 99;

    private PlayStats Stats()
    {
        var store = new IntegrationStore(database, new EphemeralDataProtectionProvider(), new ConfigurationBuilder().Build(), TimeProvider.System, NullLogger<IntegrationStore>.Instance);
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

    private static WrappedAsk Ask(ulong guild, DateTimeZone zone, IReadOnlySet<ulong> bots, ulong? member = null) => new(guild, All, zone, bots, member, id => $"#{id}");

    private static List<string> Lines(WrappedPart part, string heading)
        => [.. part.Lists.Single(l => l.Heading == heading).Lines.Select(l => $"{(l.Member is { } m ? $"<{m}>" : l.Name)} · {l.Detail}")];

    // Every section's queries run on Postgres, and say nothing for a server with nothing.
    [Fact]
    public async Task Wrapped_sections_read_an_empty_server()
    {
        IWrappedSection[] sections = [new WrappedMessages(database), new WrappedVoiceTime(database), new EventsWrapped(database),
            new PointsWrapped(database, new SettingsStore(database, TimeProvider.System)), new QuotesWrapped(database), new FameWrapped(database)];
        foreach (var section in sections)
        {
            Assert.Null(await section.PartAsync(Ask(1004, DateTimeZone.Utc, new HashSet<ulong>())));
            Assert.Null(await section.PartAsync(Ask(1004, DateTimeZone.Utc, new HashSet<ulong>(), Ana)));
        }
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
        var bots = new HashSet<ulong> { Helper };
        var server = await new WrappedMessages(database).PartAsync(Ask(guild, kathmandu, bots));
        Assert.Equal("3 messages", server!.Intro);
        Assert.Equal([("Messages", "3"), ("Busiest time", "Tuesdays around 01:00")], server.Facts);
        Assert.Equal(["#5 · 2 messages", "#6 · 1 message"], Lines(server, "Busiest channels"));
        Assert.Equal(["🎉 · 2×", "<:pog:123> · 1×"], Lines(server, "Top emoji"));
        Assert.Equal([$"<{Ana}> · 2 messages", $"<{Bo}> · 1 message"], Lines(server, "Chattiest"));
        var ana = await new WrappedMessages(database).PartAsync(Ask(guild, kathmandu, bots, Ana));
        Assert.Equal("sent 2 messages", ana!.Intro);
        Assert.Equal([("Messages", "2"), ("Busiest time", "Tuesdays around 01:00"), ("Chattiest rank", "#1 of 2")], ana.Facts);
        Assert.Equal(["#5 · 2 messages"], Lines(ana, "Your channels"));
    }

    // Earned is what counts as earned (not a bet's win); a kudos counts once, for the one who got it.
    [Fact]
    public async Task Points_in_wrapped_are_what_was_earned_and_kudos()
    {
        const ulong guild = 1007;
        await using (var db = database.CreateDbContext())
        {
            PointEntry Entry(ulong user, double amount, string kind, ulong? actor = null) => new() { GuildId = guild, UserId = user, Amount = amount, Kind = kind, ActorId = actor, CreatedAt = October };
            db.PointEntries.AddRange(
                Entry(Ana, 30, PointEntryKinds.Activity), Entry(Ana, -1, PointEntryKinds.Kudos, Bo), Entry(Bo, 1, PointEntryKinds.Kudos, Ana), Entry(Bo, 100, PointEntryKinds.Bet));
            await db.SaveChangesAsync();
        }
        var points = new PointsWrapped(database, new SettingsStore(database, TimeProvider.System));
        var server = await points.PartAsync(Ask(guild, DateTimeZone.Utc, new HashSet<ulong>()));
        Assert.Equal([("Kudos given", "1"), ("Earned", "31 points")], server!.Facts);
        Assert.Equal([$"<{Bo}> · 1 kudos"], Lines(server, "Most kudos"));
        Assert.Equal([$"<{Ana}> · 30 points", $"<{Bo}> · 1 point"], Lines(server, "Top earners"));
        var ana = await points.PartAsync(Ask(guild, DateTimeZone.Utc, new HashSet<ulong>(), Ana));
        Assert.Equal([("Kudos", "0 received, 1 given"), ("Earned", "30 points")], ana!.Facts);
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
