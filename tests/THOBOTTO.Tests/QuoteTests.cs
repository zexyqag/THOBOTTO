using THOBOTTO.Quotes;

namespace THOBOTTO.Tests;

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
