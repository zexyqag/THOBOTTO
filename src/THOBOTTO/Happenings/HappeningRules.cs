using THOBOTTO.Modules;

namespace THOBOTTO.Happenings;

// Stored with SettingsStore under the module id.
public sealed record HappeningRules
{
    [Setting("Forum", Help = "Each happening gets a post of its own there, beside its card where it was started; none, only where it was started. The bot needs Manage Threads there (to tag and close posts) and Manage Channel (to add the tags it uses).", Kind = SettingKind.ForumChannel)]
    public ulong? ForumChannelId { get; init; }

    // The kinds switched off; one added later starts on.
    [Setting("Posted there", Section = "In the forum", Kind = SettingKind.Switches, Help = "Records (Wrapped, poll and quiz results) stay open to talk about for a while (Wrapped a week, the others three days), then they're archived.")]
    public IReadOnlyList<string> Off { get; init; } = [];

    public bool Shows(string kind) => !Off.Contains(kind);
}
