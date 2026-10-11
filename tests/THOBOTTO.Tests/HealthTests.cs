using NetCord.Gateway;

using THOBOTTO.Panel;

namespace THOBOTTO.Tests;

public class HealthTests
{
    [Fact]
    public void All_well_is_no_problem()
        => Assert.Empty(Health.Problems(WebSocketStatus.Ready, database: true, relayOn: true, relayRunning: true));

    [Fact]
    public void A_relay_turned_on_but_not_running_is_reported()
        => Assert.Equal(["voice relay not running"], Health.Problems(WebSocketStatus.Ready, database: true, relayOn: true, relayRunning: false));

    [Fact]
    public void A_relay_turned_off_is_not()
        => Assert.Empty(Health.Problems(WebSocketStatus.Ready, database: true, relayOn: false, relayRunning: false));

    [Fact]
    public void Discord_and_the_database_are_reported_together()
        => Assert.Equal(["Discord: Disconnected", "database unreachable"], Health.Problems(WebSocketStatus.Disconnected, database: false, relayOn: false, relayRunning: false));
}
