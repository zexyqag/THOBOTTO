using THOBOTTO.Helpers;

namespace THOBOTTO.Tests;

public class VoiceMarksTests
{
    [Fact]
    public void A_line_without_marks_is_one_piece()
    {
        var piece = Assert.Single(VoiceMarks.Parse("Surf's up in General!"));
        Assert.Equal(new Piece("Surf's up in General!"), piece);
    }

    [Fact]
    public void Spans_and_pauses_become_pieces()
    {
        var pieces = VoiceMarks.Parse("Unit [spell]404[/spell] [pause 1s] [slow]reporting[/slow] in.");
        Assert.Equal(["Unit ", "4 0 4", "reporting", " in."], pieces.Select(p => p.Text));
        Assert.Equal(TimeSpan.FromSeconds(1), pieces[2].Pause);
        Assert.Equal(0.75, pieces[2].Speed);
        Assert.Equal(1, pieces[3].Speed);
    }

    [Fact]
    public void High_low_and_glitch_mark_their_span()
    {
        var pieces = VoiceMarks.Parse("[high]Hi[/high] [low]there[/low] [glitch]zzt[/glitch]");
        Assert.Equal(["Hi", "there", "zzt"], pieces.Select(p => p.Text));
        Assert.Equal(5, pieces[0].Pitch);
        Assert.Equal(-5, pieces[1].Pitch);
        Assert.True(pieces[2].Glitch);
        Assert.False(pieces[0].Glitch);
    }

    [Fact]
    public void Unknown_brackets_and_stray_closers_stay_text()
        => Assert.Equal("Quote [sic] [/slow] here", Assert.Single(VoiceMarks.Parse("Quote [sic] [/slow] here")).Text);

    [Fact]
    public void Chat_drops_the_marks_and_keeps_the_words()
    {
        Assert.Equal("Unit 404 reporting in.", VoiceMarks.Strip("Unit [spell]404[/spell] [pause 1s] [slow]reporting[/slow] in."));
        Assert.Equal("Quote [sic] here", VoiceMarks.Strip("Quote [sic] here"));
    }

    [Theory]
    [InlineData("[slow]never closed", "[slow] isn't closed with [/slow]")]
    [InlineData("closed [/fast] first", "[/fast] without a [fast] before it")]
    [InlineData("[slow]fine[/slow] [pause] [sic]", null)]
    public void Problems_are_named(string line, string? problem) => Assert.Equal(problem, VoiceMarks.Problem(line));

    [Theory]
    [InlineData("", 600)]
    [InlineData("250ms", 250)]
    [InlineData("1.5s", 1500)]
    [InlineData("2", 2000)]
    [InlineData("99s", 5000)]
    public void Pauses_are_read_in_seconds_or_milliseconds_and_capped(string arg, int ms)
        => Assert.Equal(TimeSpan.FromMilliseconds(ms), Assert.Single(VoiceMarks.Parse($"[pause {arg}]".Replace("[pause ]", "[pause]"))).Pause);
}
