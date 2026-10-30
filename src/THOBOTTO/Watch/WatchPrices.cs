using THOBOTTO.Modules;
using THOBOTTO.Points;

namespace THOBOTTO.Watch;

public sealed class WatchPrices(SettingsStore settings, PointsEngine points, ModuleState modules) : IPriceSource
{
    public async Task<IReadOnlyList<Price>> PricesAsync(ulong guildId)
    {
        if (!await modules.IsEnabledAsync(guildId, WatchRooms.ModuleId))
            return [];
        var rules = await settings.GetAsync<WatchRules>(guildId, WatchRooms.ModuleId);
        if (rules.AddCost <= 0)
            return [];
        var format = points.Rules(guildId);
        return [new("Adding a video to a watch party", $"{format.Format(rules.AddCost)} after your first {rules.FreeAddsPerDay} of the day"
            + (rules.CostPerWaiting > 0 ? $", {format.Format(rules.CostPerWaiting)} more for each of your videos waiting" : "")
            + (rules.FreeWithListeners > 0 ? $"; free with {rules.FreeWithListeners} or fewer watching" : ""))];
    }
}
