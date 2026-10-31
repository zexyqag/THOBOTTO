using THOBOTTO.Modules;

namespace THOBOTTO.Live;

// Where going live is announced.
public sealed class LiveSummary(SettingsStore settings, TwitchWatch twitch) : ISettingsSummary
{
    public string ModuleId => LiveWatch.ModuleId;

    public async Task<string?> NowAsync(ulong guildId, Func<ulong, string> channel)
    {
        var rules = await settings.GetAsync<LiveRules>(guildId, ModuleId);
        var followed = (await twitch.FollowedAsync(guildId)).Count;
        var who = followed == 0 ? "a member going live" : $"a member going live, or one of the {followed} Twitch channel{(followed == 1 ? "" : "s")} followed (/live follow),";
        return rules.ChannelId is { } to
            ? $"Now: {who} is announced in {channel(to)}{(rules.MentionRoleId is null ? "" : ", mentioning the role below")}, and shows in the overview channels' Live now block."
            : $"Now: going live isn't announced (no channel below); it shows only in the overview channels' Live now block{(followed == 0 ? "" : $", with the {followed} Twitch channel{(followed == 1 ? "" : "s")} followed")}.";
    }
}
