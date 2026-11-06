using NetCord.Gateway;

using THOBOTTO.Discord;
using THOBOTTO.Moderation;
using THOBOTTO.Modules;

namespace THOBOTTO.Gate;

// Where the gate's alerts go, with the moderation log.
public sealed class GateSummary(SettingsStore settings) : ISettingsSummary
{
    public string ModuleId => Gatekeeper.ModuleId;

    public async Task<string?> NowAsync(ulong guildId, Func<ulong, string> channel)
    {
        var rules = await settings.GetAsync<GateRules>(guildId, ModuleId);
        var log = (await settings.GetAsync<ModRules>(guildId, CaseBook.ModuleId)).LogChannelId;
        return rules.ReviewChannelId is { } own ? $"Now: the gate's alerts (raids, newcomers waiting) go to {channel(own)}."
            : log is { } to ? $"Now: the gate's alerts (raids, newcomers waiting) go to the moderation log, {channel(to)}."
            : "Now: the gate's alerts aren't posted: they have no channel of their own, and there's no moderation log.";
    }
}

// On the moderation page: the gate's alerts that share its log.
public sealed class GateAlertsInLogSummary(SettingsStore settings, ModuleState modules) : ISettingsSummary
{
    public string ModuleId => CaseBook.ModuleId;

    public async Task<string?> NowAsync(ulong guildId, Func<ulong, string> channel)
    {
        if (!await modules.IsEnabledAsync(guildId, Gatekeeper.ModuleId))
            return null;
        var own = (await settings.GetAsync<GateRules>(guildId, Gatekeeper.ModuleId)).ReviewChannelId;
        var log = (await settings.GetAsync<ModRules>(guildId, CaseBook.ModuleId)).LogChannelId;
        return own is null && log is not null ? "The gate's alerts are posted in the log too." : null;
    }
}

// Verifying through Discord's own rules screening, where the server isn't a Community one yet.
public sealed class GateScreeningSummary(SettingsStore settings, GatewayClient gateway) : ISettingsSummary
{
    public string ModuleId => Gatekeeper.ModuleId;

    public async Task<string?> NowAsync(ulong guildId, Func<ulong, string> channel)
        => (await settings.GetAsync<GateRules>(guildId, ModuleId)).Verify == VerifyModes.Discord && !gateway.IsCommunity(guildId)
            ? $"Newcomers come straight in: Discord's rules screening needs {Community.Needed}."
            : null;
}
