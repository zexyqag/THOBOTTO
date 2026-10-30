using THOBOTTO.Fame;
using THOBOTTO.Modules;

namespace THOBOTTO.Quotes;

// Where saved quotes go, with the hall of fame's showcase.
public sealed class QuotesSummary(SettingsStore settings) : ISettingsSummary
{
    public string ModuleId => QuoteCommands.ModuleId;

    public async Task<string?> NowAsync(ulong guildId, Func<ulong, string> channel)
    {
        var rules = await settings.GetAsync<QuoteRules>(guildId, ModuleId);
        var showcase = (await settings.GetAsync<FameRules>(guildId, HallOfFame.ModuleId)).ShowcaseChannelId;
        return !rules.PostQuotes ? "Now: saved quotes aren't posted; they're kept, and can be looked at with /quote and on the panel."
            : rules.PostsTo(showcase) is not { } to ? "Now: saved quotes aren't posted: they have no channel of their own, and the hall of fame has no showcase set."
            : to == rules.ChannelId ? $"Now: saved quotes are posted in {channel(to)}, their own channel."
            : $"Now: saved quotes are posted in the hall of fame's showcase, {channel(to)}.";
    }
}

// On the hall of fame's page: the saved quotes that share its showcase.
public sealed class QuotesInShowcaseSummary(SettingsStore settings, ModuleState modules) : ISettingsSummary
{
    public string ModuleId => HallOfFame.ModuleId;

    public async Task<string?> NowAsync(ulong guildId, Func<ulong, string> channel)
    {
        if (!await modules.IsEnabledAsync(guildId, QuoteCommands.ModuleId))
            return null;
        var rules = await settings.GetAsync<QuoteRules>(guildId, QuoteCommands.ModuleId);
        var showcase = (await settings.GetAsync<FameRules>(guildId, HallOfFame.ModuleId)).ShowcaseChannelId;
        return rules.PostQuotes && rules.ChannelId is null && showcase is not null ? "Saved quotes are posted in the showcase too (Quotes can give them a channel of their own)." : null;
    }
}
