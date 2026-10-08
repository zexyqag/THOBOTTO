namespace THOBOTTO.Mischief;

// Stored with SettingsStore under the module id. Costs only apply while the points module is on.
public sealed record MischiefRules
{
    // A rename costs RenameCost × RenameGrowth^(times the target was renamed in the window).
    public double RenameCost { get; init; } = 20;
    public double RenameGrowth { get; init; } = 2;
    public double RenameWindowHours { get; init; } = 24;
    public int RenameCooldownMinutes { get; init; } = 5;

    // Doing it to yourself is allowed, at a premium.
    public double RenameSelfMultiplier { get; init; } = 5;
    public double PaintSelfMultiplier { get; init; } = 5;

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

    public double PaintCostPerHour { get; init; } = 5;
    public int PaintMaxHours { get; init; } = 24;
    public double PaintBreakMultiplier { get; init; } = 2;

    public double BuyBackPrice(TimeSpan sinceRename)
    {
        var left = Math.Max(0, 1 - sinceRename.TotalHours / BuyBackWindowHours);
        return Whole(Math.Max(BuyBackMinCost, BuyBackCost * left));
    }

    public double LockBreakPrice(MischiefEffect nameLock, DateTimeOffset now) => BreakPrice(nameLock, now, LockBreakMultiplier);

    public double PaintBreakPrice(MischiefEffect paint, DateTimeOffset now) => BreakPrice(paint, now, PaintBreakMultiplier);

    public double PaintPrice(int hours) => Whole(PaintCostPerHour * hours);

    public double SelfRenamePrice() => Whole(RenameCost * RenameSelfMultiplier);

    public double SelfPaintPrice(int hours) => Whole(PaintCostPerHour * hours * PaintSelfMultiplier);

    // Ending an effect early costs the multiplier times the value of the time it has left.
    private static double BreakPrice(MischiefEffect effect, DateTimeOffset now, double multiplier)
    {
        var total = (effect.EndsAt - effect.CreatedAt).TotalHours;
        var left = Math.Max(0, (effect.EndsAt - now).TotalHours);
        return total <= 0 ? 0 : Whole(effect.Paid * left / total * multiplier);
    }

    public double ShieldPrice(int hours) => Whole(ShieldCostPerHour * hours);

    public double LockPrice(int hours) => Whole(LockCostPerHour * hours);

    private static double Whole(double price) => Math.Round(price, MidpointRounding.AwayFromZero);
}
