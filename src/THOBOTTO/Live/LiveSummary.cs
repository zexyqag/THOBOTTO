using THOBOTTO.Modules;

namespace THOBOTTO.Live;

// Where going live is announced.
public sealed class LiveSummary(SettingsStore settings) : ISettingsSummary
{
    public string ModuleId => LiveWatch.ModuleId;

    public async Task<string?> NowAsync(ulong guildId, Func<ulong, string> channel)
    {
        var rules = await settings.GetAsync<LiveRules>(guildId, ModuleId);
        return rules.ChannelId is { } to
            ? $"Now: a member going live is announced in {channel(to)}{(rules.MentionRoleId is null ? "" : ", mentioning the role below")}, and shows in the overview channels' Live now block."
            : "Now: going live isn't announced (no channel below); it shows only in the overview channels' Live now block.";
    }
}
