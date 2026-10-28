using THOBOTTO.Modules;
using THOBOTTO.Points;

namespace THOBOTTO.Activity.Watch;

public sealed class WatchPrices(SettingsStore settings, PointsEngine points, ModuleState modules) : IPriceSource
{
    public async Task<IReadOnlyList<Price>> PricesAsync(ulong guildId)
    {
        if (!await modules.IsEnabledAsync(guildId, WatchRooms.ModuleId))
            return [];
        var rules = await settings.GetAsync<WatchRules>(guildId, WatchRooms.ModuleId);
        return rules.AddCost <= 0 ? [] : [new("Adding a video to a watch party", $"{points.Rules(guildId).Format(rules.AddCost)} after your first {rules.FreeAddsPerDay} of the day")];
    }
}
