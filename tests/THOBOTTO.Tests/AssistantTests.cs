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
