using THOBOTTO.Games;

namespace THOBOTTO.Tests;

// What Discord took and refused on a button when every emoji was tried.
public class GameEmojiTests
{
    [Theory]
    [InlineData("🧟", "🧟", "🧟")]
    [InlineData(" 🧟 ", "🧟", "🧟")]
    [InlineData(":zombie:", "🧟", "🧟")]
    [InlineData("⚔", "⚔️", "⚔")]
    [InlineData("⚔️", "⚔️", "⚔")]
    [InlineData("™", "™️", "™")]
    [InlineData("©️", "©️", "©")]
    [InlineData("1⃣", "1️⃣", "1⃣")]
    [InlineData("👍🏽", "👍🏽", "👍🏽")]
    [InlineData("🇳🇴", "🇳🇴", "🇳🇴")]
    [InlineData("🧟‍♂️", "🧟‍♂️", "🧟‍♂️")]
    [InlineData("🏳️‍🌈", "🏳️‍🌈", "🏳️‍🌈")]
    [InlineData("🐦‍🔥", "🐦‍🔥", "🐦‍🔥")]
    [InlineData("🫩", "🫩", "🫩")]
    public void Saved_whole_and_put_on_a_button_as_Discord_has_it(string typed, string saved, string button)
    {
        Assert.Equal(saved, GameDirectory.StandardEmoji(typed));
        Assert.Equal(button, GameDirectory.ButtonEmoji(saved));
    }

    [Theory]
    [InlineData("🧟🧟")]
    [InlineData("🧟‍♂")]
    [InlineData("★")]
    [InlineData("🎮︎")]
    [InlineData("-")]
    [InlineData("zombie")]
    [InlineData("<:pog:123456789012345678>")]
    [InlineData("🫪")]
    [InlineData("🏽")]
    [InlineData("🦰")]
    [InlineData("👁️‍🗨️")]
    [InlineData("")]
    [InlineData(null)]
    public void Discord_refuses_it(string? typed)
    {
        Assert.Null(GameDirectory.StandardEmoji(typed));
        Assert.Null(GameDirectory.ButtonEmoji(typed));
    }
}
