using THOBOTTO.Modules;

namespace THOBOTTO.Live;

// Stored with SettingsStore under the module id.
public sealed record LiveRules
{
    [Setting("Announce in", Help = "When a member goes live, it's posted here, and changed to how long they were live once they stop. None: only the overview channels' Live now block shows it.", Kind = SettingKind.TextChannel)]
    public ulong? ChannelId { get; init; }

    [Setting("Mention", Help = "Optional. A role mentioned with each one, for those who want to hear of it.", Kind = SettingKind.Role)]
    public ulong? MentionRoleId { get; init; }

    [Setting("Only members with", Help = "Optional. Only their streams are shown; none: everyone's.", Kind = SettingKind.Role)]
    public ulong? StreamerRoleId { get; init; }
}
