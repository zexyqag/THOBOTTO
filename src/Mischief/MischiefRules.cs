namespace THOBOTTO.Mischief;

// Stored with SettingsStore under the module id. Costs only apply while the points module is on.
public sealed record MischiefRules
{
    // A rename costs RenameCost × RenameGrowth^(times the target was renamed in the window).
    public double RenameCost { get; init; } = 20;
    public double RenameGrowth { get; init; } = 2;
    public double RenameWindowHours { get; init; } = 24;
    public int RenameCooldownMinutes { get; init; } = 5;

    public double RenamePrice(int recentRenames) => RenameCost * Math.Pow(RenameGrowth, recentRenames);
}
