using THOBOTTO.Expressions;
using THOBOTTO.Kaomoji;
using THOBOTTO.Sounds;

namespace THOBOTTO.Tests;

public class ExpressionReachTests
{
    [Fact]
    public void A_known_face_in_a_message_is_the_one_to_save()
    {
        var (text, known) = KaomojiLibrary.Guess("lol ¯\\_(ツ)_/¯ whatever", KaomojiLibrary.All);

        Assert.Equal("¯\\_(ツ)_/¯", text);
        Assert.Equal("Shrug", known?.Name);
    }

    [Fact]
    public void Else_the_longest_run_of_face_like_words()
    {
        Assert.Equal("(o_o)>", KaomojiLibrary.Guess("ok then (o_o)> sure!", []).Text);
        Assert.Equal("( ͡~ ͜ʖ ͡~)", KaomojiLibrary.Guess("well ( ͡~ ͜ʖ ͡~) hm https://x.com/a", []).Text);
        Assert.Equal("just words here", KaomojiLibrary.Guess("just words here", []).Text);
        Assert.Equal("(｡◕‿‿◕｡)/~", KaomojiLibrary.Guess("hey (｡◕‿‿◕｡)/~ there smoke 20261104-172839", []).Text);
    }

    [Fact]
    public void Emoji_from_elsewhere_in_a_message_are_found_once_and_the_servers_own_left()
        => Assert.Equal([(5UL, true, "party"), (7UL, false, "cat")],
            ProposeMenu.Foreign("<a:party:5> <:cat:7> <:ours:9> <a:party:5> :plain:", new Dictionary<ulong, NetCord.GuildEmoji> { [9] = null! }));

    [Fact]
    public void Sticker_and_big_words_are_found_but_not_inside_others()
    {
        Assert.Equal(["happy_cat"], ExpressionPictures.Word("sticker").Matches("look sticker:happy_cat x:sticker:no").Select(m => m.Groups[1].Value));
        Assert.Equal(["partyparrot"], ExpressionPictures.Word("big").Matches("big:partyparrot https://a.b/big:no").Select(m => m.Groups[1].Value));
    }
}
