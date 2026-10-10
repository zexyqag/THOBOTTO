using THOBOTTO.Assistant;

namespace THOBOTTO.Tests;

public class AssistantTests
{
    private static Person P(string name, Whereabouts where, double score = 1) => new(0, name, where, false, score);

    [Fact]
    public void Someone_in_the_call_wins_over_namesakes_elsewhere()
        => Assert.Equal("Ana", Assert.Single(PeopleFinder.Pick([P("Ana", Whereabouts.Offline), P("Ana", Whereabouts.InTheCall)])).Name);

    [Fact]
    public void Namesakes_equally_at_hand_are_asked_about()
        => Assert.Equal(2, PeopleFinder.Pick([P("Ana", Whereabouts.Online), P("Ana B", Whereabouts.Online, 0.95), P("Anna", Whereabouts.Offline)]).Count);

    [Fact]
    public void A_much_closer_name_needs_no_question()
        => Assert.Equal("Ana", Assert.Single(PeopleFinder.Pick([P("Ana", Whereabouts.Online), P("Anja", Whereabouts.Online, 0.75)])).Name);

    [Theory]
    [InlineData("Anna", "Ana", 0.75)]
    [InlineData("ana", "Ana Smith", 1)]
    [InlineData("bob", "Ana", 0)]
    public void Names_match_whole_or_by_word(string spoken, string name, double atLeast)
        => Assert.True(PeopleFinder.Score(spoken, [name]) >= atLeast && (atLeast > 0 || PeopleFinder.Score(spoken, [name]) < 0.5));

    private static readonly string[] Two = ["Ana (in the call)", "Ana Smith (offline)"];

    [Theory]
    [InlineData("the one in the call", 0)]
    [InlineData("the offline one", 1)]
    [InlineData("second", 1)]
    [InlineData("the first one", 0)]
    [InlineData("Ana Smith", 1)]
    [InlineData("no", VoiceAnswers.Cancel)]
    [InlineData("never mind", VoiceAnswers.Cancel)]
    public void Spoken_answers_pick_a_choice(string said, int expected)
        => Assert.Equal(expected, VoiceAnswers.Pick(said, Two));

    [Fact]
    public void Yes_needs_a_single_choice()
    {
        Assert.Equal(0, VoiceAnswers.Pick("yes please", ["Give Ana 5 points"]));
        Assert.Null(VoiceAnswers.Pick("yes", Two));
    }

    [Fact]
    public void Other_talk_is_no_answer()
        => Assert.Null(VoiceAnswers.Pick("what are we playing tonight", Two));
}

public class QuotePickerTests
{
    private static readonly THOBOTTO.Listening.Said[] Recent =
    [
        new(1, "we are absolutely not losing this round", DateTimeOffset.UnixEpoch),
        new(2, "famous last words", DateTimeOffset.UnixEpoch),
        new(1, "watch me", DateTimeOffset.UnixEpoch),
    ];

    [Theory]
    [InlineData("that", 2, 2, 3)]
    [InlineData("", 1, 1, 2)]
    [InlineData("me", 1, 2, 3)]
    [InlineData("the last two lines", 2, 1, 3)]
    [InlineData("last 5", 2, 0, 3)]
    public void Quotes_take_the_right_lines(string said, ulong asker, int start, int end)
        => Assert.Equal((start, end), THOBOTTO.Quotes.QuotePicker.Pick(Recent, asker, THOBOTTO.Quotes.QuotePicker.Read(said)));

    [Fact]
    public void A_name_is_looked_up()
        => Assert.Equal(new THOBOTTO.Quotes.QuoteWho.Named("ana"), THOBOTTO.Quotes.QuotePicker.Read("Ana"));

    [Fact]
    public void Nothing_said_by_others_quotes_nothing()
        => Assert.Null(THOBOTTO.Quotes.QuotePicker.Pick([new(1, "hi", DateTimeOffset.UnixEpoch)], 1, new THOBOTTO.Quotes.QuoteWho.Others()));
}

public class LikenessTests
{
    [Theory]
    [InlineData("friday raid", "Friday night raid Fri 20:00", 0.9)]
    [InlineData("yes", "Yes", 1)]
    [InlineData("drake", "Will Drake drop an album?", 0.9)]
    public void Said_matches_titles_by_their_words(string said, string text, double atLeast)
        => Assert.True(THOBOTTO.Assistant.Likeness.Of(said, text) >= atLeast);

    [Fact]
    public void Unrelated_words_dont_match()
        => Assert.True(THOBOTTO.Assistant.Likeness.Of("movie night", "Friday raid") < 0.6);
}
