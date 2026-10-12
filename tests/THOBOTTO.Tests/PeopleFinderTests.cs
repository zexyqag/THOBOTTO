using THOBOTTO.Voice.Commands;

namespace THOBOTTO.Tests;

public class PeopleFinderTests
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
}

public class LikenessTests
{
    [Theory]
    [InlineData("friday raid", "Friday night raid Fri 20:00", 0.9)]
    [InlineData("yes", "Yes", 1)]
    [InlineData("drake", "Will Drake drop an album?", 0.9)]
    public void Said_matches_titles_by_their_words(string said, string text, double atLeast)
        => Assert.True(Likeness.Of(said, text) >= atLeast);

    [Fact]
    public void Unrelated_words_dont_match()
        => Assert.True(Likeness.Of("movie night", "Friday raid") < 0.6);
}
