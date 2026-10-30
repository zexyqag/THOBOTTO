using THOBOTTO.Modules;

namespace THOBOTTO.Moderation;

// Where cases and ban appeals go.
public sealed class ModerationSummary(SettingsStore settings) : ISettingsSummary
{
    public string ModuleId => CaseBook.ModuleId;

    public async Task<string?> NowAsync(ulong guildId, Func<ulong, string> channel)
    {
        var rules = await settings.GetAsync<ModRules>(guildId, ModuleId);
        var cases = rules.LogChannelId is { } log ? $"Now: every case is posted in {channel(log)}" : "Now: cases aren't posted (no moderation log)";
        var appeals = !rules.TakeAppeals ? "ban appeals aren't taken"
            : rules.AppealsTo() is not { } to ? "ban appeals are taken but have nowhere to go (no channel of their own, no moderation log)"
            : rules.AppealsChannelId is null ? "ban appeals go there too"
            : $"ban appeals go to {channel(to)}";
        return $"{cases}; {appeals}.";
    }
}
