using NetCord.Gateway;

using THOBOTTO.Panel;

namespace THOBOTTO.Tests;

public class HealthTests
{
    [Fact]
    public void All_well_is_no_problem()
        => Assert.Empty(Health.Problems(WebSocketStatus.Ready, database: true, relayOn: true, relayRunning: true, trustTried: true, pluginsTried: true));

    [Fact]
    public void A_relay_turned_on_but_not_running_is_reported()
        => Assert.Equal(["voice relay not running"], Health.Problems(WebSocketStatus.Ready, database: true, relayOn: true, relayRunning: false, trustTried: true, pluginsTried: true));

    [Fact]
    public void A_relay_turned_off_is_not()
        => Assert.Empty(Health.Problems(WebSocketStatus.Ready, database: true, relayOn: false, relayRunning: false, trustTried: true, pluginsTried: true));

    [Fact]
    public void Plugins_not_seen_to_yet_are_reported()
        => Assert.Equal(["Lavalink plugins not seen to yet"], Health.Problems(WebSocketStatus.Ready, database: true, relayOn: false, relayRunning: false, trustTried: true, pluginsTried: false));

    [Fact]
    public void Discord_and_the_database_are_reported_together()
        => Assert.Equal(["Discord: Disconnected", "database unreachable"], Health.Problems(WebSocketStatus.Disconnected, database: false, relayOn: false, relayRunning: false, trustTried: true, pluginsTried: true));

    [Fact]
    public void The_trust_store_has_to_be_written_first()
        => Assert.Equal(["Lavalink's trust store not written yet"], Health.Problems(WebSocketStatus.Ready, database: true, relayOn: false, relayRunning: false, trustTried: false, pluginsTried: true));
}
