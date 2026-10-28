using THOBOTTO.Modules;
using THOBOTTO.Points;

namespace THOBOTTO.Mischief;

// The mischief prices as members see them: renames, buying a name back, shields, locks, colours, bets.
public sealed class MischiefPrices(SettingsStore settings, PointsEngine points, ModuleState modules) : IPriceSource
{
    public async Task<IReadOnlyList<Price>> PricesAsync(ulong guildId)
    {
        if (!await modules.IsEnabledAsync(guildId, MischiefModule.ModuleId))
            return [];
        var rules = await settings.GetAsync<MischiefRules>(guildId, MischiefModule.ModuleId);
        var money = points.Rules(guildId);
        return
        [
            new("Renaming someone", $"{money.Format(rules.RenameCost)}, × {rules.RenameGrowth:0.#} for each rename they had in the last {rules.RenameWindowHours:0.#} hours; yourself × {rules.RenameSelfMultiplier:0.#}"),
            new("Buying your name back", $"{money.Format(rules.BuyBackCost)} right after a rename, down to {money.Format(rules.BuyBackMinCost)} over {rules.BuyBackWindowHours:0.#} hours"),
            new("A shield", $"{money.Format(rules.ShieldCostPerHour)} an hour, up to {rules.ShieldMaxHours} hours"),
            new("A name lock", $"{money.Format(rules.LockCostPerHour)} an hour, up to {rules.LockMaxHours} hours; breaking one costs {rules.LockBreakMultiplier:0.#} × what's left"),
            new("A name colour", $"{money.Format(rules.PaintCostPerHour)} an hour, up to {rules.PaintMaxHours} hours; yourself × {rules.PaintSelfMultiplier:0.#}"),
            new("A bet", $"the creator takes {rules.BetCreatorCutPercent:0.#}% of the pool{(rules.BetHouseCutPercent > 0 ? $", the house {rules.BetHouseCutPercent:0.#}%" : "")}; stakes up to {money.Format(rules.BetMaxStake)}"),
        ];
    }
}
