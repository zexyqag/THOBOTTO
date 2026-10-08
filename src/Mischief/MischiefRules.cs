namespace THOBOTTO.Mischief;

// Stored with SettingsStore under the module id. Costs only apply while the points module is on.
public sealed record MischiefRules
{
    // A rename costs RenameCost × RenameGrowth^(times the target was renamed in the window).
    public double RenameCost { get; init; } = 20;
    public double RenameGrowth { get; init; } = 2;
    public double RenameWindowHours { get; init; } = 24;
    public int RenameCooldownMinutes { get; init; } = 5;

    // Prices are whole points, so what is quoted is what is charged.
    public double RenamePrice(int recentRenames) => Whole(RenameCost * Math.Pow(RenameGrowth, recentRenames));

    // Buying your own name back: BuyBackCost right after a rename, falling linearly to
    // BuyBackMinCost over BuyBackWindowHours.
    public double BuyBackCost { get; init; } = 40;
    public double BuyBackMinCost { get; init; } = 5;
    public double BuyBackWindowHours { get; init; } = 24;

    public double ShieldCostPerHour { get; init; } = 10;
    public int ShieldMaxHours { get; init; } = 24;

    public double LockCostPerHour { get; init; } = 10;
    public int LockMaxHours { get; init; } = 24;

    // Breaking a lock costs this many times the value of the time it has left.
    public double LockBreakMultiplier { get; init; } = 2;

    public double BuyBackPrice(TimeSpan sinceRename)
    {
        var left = Math.Max(0, 1 - sinceRename.TotalHours / BuyBackWindowHours);
        return Whole(Math.Max(BuyBackMinCost, BuyBackCost * left));
    }

    public double LockBreakPrice(MischiefEffect nameLock, DateTimeOffset now)
    {
        var total = (nameLock.EndsAt - nameLock.CreatedAt).TotalHours;
        var left = Math.Max(0, (nameLock.EndsAt - now).TotalHours);
        return total <= 0 ? 0 : Whole(nameLock.Paid * left / total * LockBreakMultiplier);
    }

    public double ShieldPrice(int hours) => Whole(ShieldCostPerHour * hours);

    public double LockPrice(int hours) => Whole(LockCostPerHour * hours);

    private static double Whole(double price) => Math.Round(price, MidpointRounding.AwayFromZero);
}
