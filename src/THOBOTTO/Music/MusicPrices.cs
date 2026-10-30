using THOBOTTO.Modules;
using THOBOTTO.Points;

namespace THOBOTTO.Music;

public sealed class MusicPrices(SettingsStore settings, PointsEngine points, ModuleState modules) : IPriceSource
{
    public async Task<IReadOnlyList<Price>> PricesAsync(ulong guildId)
    {
        if (!await modules.IsEnabledAsync(guildId, MusicService.ModuleId))
            return [];
        var rules = await settings.GetAsync<MusicRules>(guildId, MusicService.ModuleId);
        if (rules.AddCost <= 0)
            return [];
        var format = points.Rules(guildId);
        return [new("Adding a song or playlist", $"{format.Format(rules.AddCost)} after your first {rules.FreeAddsPerDay} of the day"
            + (rules.CostPerWaiting > 0 ? $", {format.Format(rules.CostPerWaiting)} more for each of your songs waiting" : "")
            + (rules.PlaylistCostsLike > 1 ? $"; a playlist like {rules.PlaylistCostsLike} songs" : "")
            + (rules.FreeWithListeners > 0 ? $"; free with {rules.FreeWithListeners} or fewer listening" : ""))];
    }
}
