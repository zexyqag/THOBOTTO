using THOBOTTO.Modules;

namespace THOBOTTO.Fame;

public sealed class FameSummary(SettingsStore settings) : ISettingsSummary
{
    public string ModuleId => HallOfFame.ModuleId;

    public async Task<string?> NowAsync(ulong guildId, Func<ulong, string> channel)
        => (await settings.GetAsync<FameRules>(guildId, ModuleId)).ShowcaseChannelId is { } showcase
            ? $"Now: highlights are posted in {channel(showcase)}."
            : "Now: nothing is tracked or posted until a showcase channel is set.";
}
