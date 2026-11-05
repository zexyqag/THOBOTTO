using THOBOTTO.Discord;
using THOBOTTO.Voice.Channels;

namespace THOBOTTO.Tests;

public class DynamicVoiceTests
{
    [Fact]
    public void People_in_a_hub_get_a_channel_and_a_bot_there_stays()
    {
        var present = new Dictionary<ulong, VoicePresenceEntry>
        {
            [1] = new(10, false, false, false),
            [2] = new(10, false, true, false),
            [3] = new(11, false, false, false),
        };

        Assert.Equal([(1UL, 10UL)], DynamicVoice.Arrivals(present, new HashSet<ulong> { 10 }));
    }
}
