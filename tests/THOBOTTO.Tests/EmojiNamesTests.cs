using THOBOTTO.Expressions;
using THOBOTTO.Reposts;

namespace THOBOTTO.Tests;

public class EmojiNamesTests
{
    private static IEnumerable<string> Found(string text)
    {
        var found = new List<string>();
        RepostText.OutsideCode(text, part =>
        {
            found.AddRange(EmojiFinder.Names().Matches(part).Select(m => m.Groups[1].Value));
            return part;
        });
        return found;
    }

    [Fact]
    public void A_name_between_colons_is_found()
        => Assert.Equal(["partyparrot", "Cat_2"], Found("hey :partyparrot: and :Cat_2:!"));

    [Fact]
    public void Emoji_already_made_times_short_names_and_code_are_left()
        => Assert.Empty(Found("<:party:123> <a:party:456> at 10:30:45 :a: `:party:` ```\n:party:\n``` x:party:"));

    [Fact]
    public void Text_outside_code_is_rewritten_and_code_kept()
        => Assert.Equal("A `a` A ```a``` A", RepostText.OutsideCode("a `a` a ```a``` a", part => part.ToUpperInvariant()));
}
