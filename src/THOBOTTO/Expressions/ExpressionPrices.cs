using THOBOTTO.Modules;
using THOBOTTO.Points;

namespace THOBOTTO.Expressions;

// What proposing costs and earns: emojis, stickers and sounds share these rules.
public sealed class ExpressionPrices(SettingsStore settings, PointsEngine points, ModuleState modules) : IPriceSource
{
    public async Task<IReadOnlyList<Price>> PricesAsync(ulong guildId)
    {
        if (!await modules.IsEnabledAsync(guildId, ExpressionShelf.ModuleId))
            return [];
        var rules = await settings.GetAsync<ExpressionRules>(guildId, ExpressionShelf.ModuleId);
        var money = points.Rules(guildId);
        return
        [
            new("Proposing an emoji, sticker or sound", $"{money.Format(rules.ProposeCost)}, refunded if accepted"),
            new("Having one accepted", $"{money.Format(rules.AcceptBonus)} to its maker"),
            new("Royalties", $"{rules.RoyaltyPerUse:0.##} each time someone else uses yours, at most {money.Format(rules.RoyaltyDailyCap)} a day"),
        ];
    }
}
