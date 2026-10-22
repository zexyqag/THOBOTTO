using THOBOTTO.Voice.Speaking;

namespace THOBOTTO.Tests;

public class SpokenNamesTests
{
    [Fact]
    public void Mentions_are_said_as_names()
        => Assert.Equal("Surf's up in surf spot! Hi Ana and someone, see a channel.",
            HelperSpeech.Named("Surf's up in <#5>! Hi <@1> and <@!2>, see <#9>.", id => id == 1 ? "Ana" : null, id => id == 5 ? "surf-spot" : null));
}
