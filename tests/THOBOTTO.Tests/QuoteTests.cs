using THOBOTTO.Quotes;
using THOBOTTO.Voice.Hearing;

namespace THOBOTTO.Tests;

public class QuotePickerTests
{
    private static readonly Said[] Recent =
    [
        new(1, "we are absolutely not losing this round", DateTimeOffset.UnixEpoch, 1),
        new(2, "famous last words", DateTimeOffset.UnixEpoch, 2),
        new(1, "watch me", DateTimeOffset.UnixEpoch, 3),
    ];

    [Theory]
    [InlineData("that", 2, 2, 3)]
    [InlineData("", 1, 1, 2)]
    [InlineData("me", 1, 2, 3)]
    [InlineData("the last two lines", 2, 1, 3)]
    [InlineData("last 5", 2, 0, 3)]
    public void Quotes_take_the_right_lines(string said, ulong asker, int start, int end)
        => Assert.Equal((start, end), QuotePicker.Pick(Recent, asker, QuotePicker.Read(said)));

    [Fact]
    public void A_name_is_looked_up()
        => Assert.Equal(new QuoteWho.Named("ana"), QuotePicker.Read("Ana"));

    [Fact]
    public void Nothing_said_by_others_quotes_nothing()
        => Assert.Null(QuotePicker.Pick([new(1, "hi", DateTimeOffset.UnixEpoch, 1)], 1, new QuoteWho.Others()));
}

public class QuoteTests
{
    [Fact]
    public void Parses_one_turn_per_line()
    {
        var lines = QuoteText.Parse("Marty: I'll just check the basement\n@goldfish: please don't");

        Assert.Equal([new("Marty", "I'll just check the basement"), new("goldfish", "please don't")], lines);
    }

    [Fact]
    public void A_line_without_a_name_continues_the_previous_turn()
    {
        var lines = QuoteText.Parse("Marty: first\nsecond");

        Assert.Equal("first\nsecond", Assert.Single(lines!).Text);
    }

    [Fact]
    public void Colons_in_links_are_not_speakers()
    {
        var lines = QuoteText.Parse("Marty: look\nhttps://example.com");

        Assert.Equal("look\nhttps://example.com", Assert.Single(lines!).Text);
    }

    [Theory]
    [InlineData("no speaker at all")]
    [InlineData("Marty:")]
    [InlineData(": nobody")]
    public void Rejects_text_that_isnt_quote_lines(string text)
    {
        Assert.Null(QuoteText.Parse(text));
    }

    [Fact]
    public void Rejects_too_many_lines()
    {
        Assert.Null(QuoteText.Parse(string.Join('\n', Enumerable.Range(0, QuoteText.MaxLines + 1).Select(i => $"A: {i}"))));
    }
}
