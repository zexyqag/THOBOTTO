using THOBOTTO.Events;
using THOBOTTO.Games;

namespace THOBOTTO.Tests;

public class GameRulesTests
{
    [Fact]
    public void Sessions_get_an_open_voice_channel_unless_set_otherwise()
    {
        Assert.Equal(VoiceModes.Open, new GameRules().UsualVoice);
        Assert.Equal(VoiceModes.Locked, new GameRules { SessionVoice = VoiceModes.Locked }.UsualVoice);
        Assert.Null(new GameRules { SessionVoice = GameRules.NoVoice }.UsualVoice);
    }
}
