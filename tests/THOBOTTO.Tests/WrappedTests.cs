using NodaTime;

using THOBOTTO.Music;
using THOBOTTO.Wrapped;

namespace THOBOTTO.Tests;

public class WrappedTests
{
    private static readonly DateTimeZone Copenhagen = DateTimeZoneProviders.Tzdb["Europe/Copenhagen"];
    private static readonly Instant Now = Instant.FromUtc(2026, 1, 15, 12, 0);

    [Fact]
    public void Periods_follow_the_server_time_zone()
    {
        var month = WrappedSpan.For(WrappedPeriod.ThisMonth, Copenhagen, Now);
        Assert.Equal("January 2026", month.Label);
        // UTC, as the database needs.
        Assert.Equal(new DateTimeOffset(2025, 12, 31, 23, 0, 0, TimeSpan.Zero), month.From);
        Assert.Equal(TimeSpan.Zero, month.From.Offset);

        var lastMonth = WrappedSpan.For(WrappedPeriod.LastMonth, Copenhagen, Now);
        Assert.Equal("December 2025", lastMonth.Label);
        Assert.Equal(month.From, lastMonth.To);

        Assert.Equal("2025", WrappedSpan.For(WrappedPeriod.LastYear, Copenhagen, Now).Label);
        Assert.Equal("all time", WrappedSpan.For(WrappedPeriod.AllTime, Copenhagen, Now).Label);
    }

    [Fact]
    public void Genres_leave_out_tags_that_arent_genres()
        => Assert.Equal(["hard rock", "rock", "classic rock"], GenreBook.Pick("AC/DC",
        [
            ("hard rock", 100), ("seen live", 90), ("rock", 80), ("AC/DC", 60), ("australian", 50), ("classic rock", 45), ("heavy metal", 40), ("80s", 10),
        ]));
}

public class WrappedMessagesTests
{
    [Fact]
    public void Emoji_are_counted_once_per_message()
        => Assert.Equal(["<:pog:123>", "<a:dance:456>", "🎉", "❤"], WrappedMessages.EmojiIn("gg <:pog:123> <:pog:123> 🎉🎉 <a:dance:456> ❤️ :not_one:").ToList());

    [Fact]
    public void Plain_text_has_no_emoji()
        => Assert.Empty(WrappedMessages.EmojiIn("just words, 100% (no emoji) — ok?"));
}

// How Wrapped puts the features' parts together.
public class WrappedCardTests
{
    private static readonly WrappedPart Messages = new("12 messages", [("Messages", "12")],
        [new("Chattiest", [WrappedLine.Of(1, "8 messages"), WrappedLine.Of(2, "3 messages"), WrappedLine.Of(3, "1 message")], 2), new("Top emoji", [])]);

    private static readonly WrappedPart Voice = new("3 hours in voice", [("Hours in voice", "3 hours")], [new("Top genres", [new("rock"), new("pop")])]);

    [Fact]
    public void Only_members_who_asked_are_named_and_as_many_as_the_list_shows()
    {
        var card = WrappedService.Card("💬", null, null, [Messages, Voice], new HashSet<ulong> { 1, 3 }, id => $"member {id}");
        Assert.Equal([("Messages", "12"), ("Hours in voice", "3 hours")], card.Facts);
        // Top emoji had nothing: left out.
        Assert.Equal(["Chattiest", "Top genres"], card.Lists.Select(l => l.Heading));
        Assert.Equal(["1. member 1 · 8 messages", "2. member 3 · 1 message"], card.Lists[0].Lines);
        Assert.Equal(["1. rock", "2. pop"], card.Lists[1].Lines);
    }

    [Fact]
    public void The_first_line_joins_the_parts_phrases()
    {
        Assert.Equal("12 messages, 3 hours in voice.", WrappedService.Intro([Messages, Voice], null));
        Assert.Equal("You sent 12 messages and spent 3 hours in voice.",
            WrappedService.Intro([Messages with { Intro = "sent 12 messages" }, Voice with { Intro = "spent 3 hours in voice" }], "Your"));
        Assert.Equal("You heard 4 songs, 1 hour of music.", WrappedService.Intro([new("heard 4 songs, 1 hour of music", [], [])], "Your"));
        Assert.Null(WrappedService.Intro([new(null, [("Events held", "2")], [])], null));
    }
}
