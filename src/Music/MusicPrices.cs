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
        return rules.AddCost <= 0 ? [] : [new("Adding a song or playlist", $"{points.Rules(guildId).Format(rules.AddCost)} after your first {rules.FreeAddsPerDay} of the day")];
    }
}
