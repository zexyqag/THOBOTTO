using THOBOTTO.Overview;

namespace THOBOTTO.Points;

// The top of the leaderboard.
public sealed class LeaderboardWidget(PointsEngine points) : IWidgetKind
{
    public string Id => "leaderboard";

    public string Name => "Leaderboard";

    public string Description => "the members with the most points";

    public IReadOnlyList<WidgetOption> Options => [new("count", "How many", "10", "1 to 25")];

    public TimeSpan Refresh => TimeSpan.FromMinutes(5);

    public async Task<WidgetView?> RenderAsync(ulong guildId, IReadOnlyDictionary<string, string> options)
    {
        var top = await points.TopAsync(guildId, WidgetOptions.Int(options, "count", 10, 1, 25));
        if (top.Count == 0)
            return null;
        var rules = points.Rules(guildId);
        var lines = top.Select((s, i) => $"{i + 1}. <@{s.UserId}> · {rules.Format(s.Balance)}");
        return new(null, new() { Title = $"Leaderboard: {rules.CurrencyName}", Description = string.Join('\n', lines), Color = new(0xF1C40F) });
    }
}
