using THOBOTTO.Characters;

namespace THOBOTTO.Tests;

public class CharacterTests
{
    private static Character C(long id, string prefix, string suffix = "") => new() { Id = id, Name = $"c{id}", Prefix = prefix, Suffix = suffix };

    [Fact]
    public void Brackets_are_what_goes_around_the_word_text()
    {
        Assert.Equal(("g:", ""), CharacterBook.Parse("g:text"));
        Assert.Equal(("[", "]"), CharacterBook.Parse("[text]"));
        Assert.Equal(("", " -g"), CharacterBook.Parse("text -g"));
        Assert.Null(CharacterBook.Parse("text"));
        Assert.Null(CharacterBook.Parse("g:"));
        Assert.Null(CharacterBook.Parse("text and text"));
    }

    [Fact]
    public void A_message_in_brackets_goes_to_the_most_specific_character()
    {
        IReadOnlyList<Character> mine = [C(1, "g:"), C(2, "gg:"), C(3, "[", "]")];

        Assert.Equal((2L, "hello"), CharacterBook.In(mine, "GG: hello") is { } a ? (a.Character.Id, a.Text) : default);
        Assert.Equal((1L, "hi"), CharacterBook.In(mine, "g:hi") is { } b ? (b.Character.Id, b.Text) : default);
        Assert.Equal((3L, "whisper"), CharacterBook.In(mine, "[whisper]") is { } c ? (c.Character.Id, c.Text) : default);
        Assert.Null(CharacterBook.In(mine, "just me [not]x"));
    }
}
