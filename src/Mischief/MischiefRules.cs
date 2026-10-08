using THOBOTTO.Modules;

namespace THOBOTTO.Mischief;

// Stored with SettingsStore under the module id. Costs only apply while the points module is on.
public sealed record MischiefRules
{
    [Setting("Cost", Help = "× growth for each time the target was renamed in the window.", Section = "Rename", Unit = "points", Min = 0, Max = 1_000_000)]
    public double RenameCost { get; init; } = 20;

    [Setting("Growth", Section = "Rename", Min = 1, Max = 100)]
    public double RenameGrowth { get; init; } = 2;

    [Setting("Window", Section = "Rename", Unit = "hours", Min = 0, Max = 720)]
    public double RenameWindowHours { get; init; } = 24;

    [Setting("Cooldown", Section = "Rename", Unit = "minutes", Min = 0, Max = 10080)]
    public int RenameCooldownMinutes { get; init; } = 5;

    [Setting("Yourself costs", Section = "Rename", Unit = "× the cost", Min = 1, Max = 1000)]
    public double RenameSelfMultiplier { get; init; } = 5;

    [Setting("Yourself costs", Section = "Name colour", Unit = "× the cost", Min = 1, Max = 1000)]
    public double PaintSelfMultiplier { get; init; } = 5;

    // Prices are whole points, so what is quoted is what is charged.
    public double RenamePrice(int recentRenames) => Whole(RenameCost * Math.Pow(RenameGrowth, recentRenames));

    [Setting("Cost right after a rename", Help = "Falls to the lowest cost over the window.", Section = "Buy your name back", Unit = "points", Min = 0, Max = 1_000_000)]
    public double BuyBackCost { get; init; } = 40;

    [Setting("Lowest cost", Section = "Buy your name back", Unit = "points", Min = 0, Max = 1_000_000)]
    public double BuyBackMinCost { get; init; } = 5;

    [Setting("Window", Section = "Buy your name back", Unit = "hours", Min = 1, Max = 720)]
    public double BuyBackWindowHours { get; init; } = 24;

    [Setting("Cost", Section = "Shield", Unit = "points an hour", Min = 0, Max = 1_000_000)]
    public double ShieldCostPerHour { get; init; } = 10;

    [Setting("Longest", Section = "Shield", Unit = "hours", Min = 1, Max = 168)]
    public int ShieldMaxHours { get; init; } = 24;

    [Setting("Cost", Section = "Name lock", Unit = "points an hour", Min = 0, Max = 1_000_000)]
    public double LockCostPerHour { get; init; } = 10;

    [Setting("Longest", Section = "Name lock", Unit = "hours", Min = 1, Max = 168)]
    public int LockMaxHours { get; init; } = 24;

    [Setting("Breaking it costs", Help = "Times the value of the time it has left.", Section = "Name lock", Unit = "×", Min = 0, Max = 100)]
    public double LockBreakMultiplier { get; init; } = 2;

    [Setting("Cost", Section = "Name colour", Unit = "points an hour", Min = 0, Max = 1_000_000)]
    public double PaintCostPerHour { get; init; } = 5;

    [Setting("Longest", Section = "Name colour", Unit = "hours", Min = 1, Max = 168)]
    public int PaintMaxHours { get; init; } = 24;

    [Setting("Removing it early costs", Help = "Times the value of the time it has left.", Section = "Name colour", Unit = "×", Min = 0, Max = 100)]
    public double PaintBreakMultiplier { get; init; } = 2;

    [Setting("Creator's cut of the pool", Section = "Bets", Unit = "%", Min = 0, Max = 50)]
    public double BetCreatorCutPercent { get; init; } = 5;

    [Setting("House cut", Help = "Paid to nobody.", Section = "Bets", Unit = "%", Min = 0, Max = 50)]
    public double BetHouseCutPercent { get; init; }

    [Setting("Creators may bet", Help = "Then someone with bets.manage has to resolve their bets.", Section = "Bets")]
    public bool BetCreatorCanBet { get; init; }

    [Setting("Largest stake", Section = "Bets", Unit = "points", Min = 1, Max = 1_000_000)]
    public double BetMaxStake { get; init; } = 100;

    [Setting("Longest bet", Section = "Bets", Unit = "days", Min = 1, Max = 365)]
    public int BetMaxDays { get; init; } = 7;

    [Setting("Cancel unresolved bets after", Help = "Counted from when betting closed; stakes are refunded.", Section = "Bets", Unit = "days", Min = 1, Max = 365)]
    public int BetAutoCancelDays { get; init; } = 3;

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
