using THOBOTTO.Expressions;

namespace THOBOTTO.Tests;

public class ExpressionSyncTests
{
    private static Expression Row(long id, string state, ulong? discordId) => new() { Id = id, Kind = ExpressionKinds.Emoji, Name = $"e{id}", Image = [], ContentType = "image/png", State = state, DiscordId = discordId };

    private static DiscordExpression Remote(ulong id) => new(id, ExpressionKinds.Emoji, $"d{id}", null, 7, false, "image/png", DateTimeOffset.UnixEpoch);

    [Fact]
    public void Unknown_ones_on_discord_are_adopted_and_live_ones_gone_there_are_retired()
    {
        var rows = new[] { Row(1, ExpressionStates.Live, 10), Row(2, ExpressionStates.Live, 20), Row(3, ExpressionStates.Retired, null), Row(4, ExpressionStates.Rejected, 40) };
        var plan = ExpressionSync.Plan(new HashSet<ulong> { 10, 30, 50 }, [Remote(10), Remote(30), Remote(40)], rows);
        Assert.Equal([2L], plan.Gone);
        Assert.Equal([30UL], plan.Unknown.Select(u => u.Id));
    }
}
