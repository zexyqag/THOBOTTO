using THOBOTTO.Modules;

namespace THOBOTTO.Fame;

// Stored with SettingsStore under the module id.
public sealed record FameRules
{
    [Setting("Showcase channel", Help = "Nothing is tracked until it's set.", Kind = SettingKind.TextChannel)]
    public ulong? ShowcaseChannelId { get; init; }

    [Setting("People who must react", Help = "Different people, not the author and not bots.", Min = 1, Max = 1000)]
    public int Threshold { get; init; } = 5;

    [Setting("Bonus for the author", Unit = "points", Min = 0, Max = 1_000_000)]
    public double Bonus { get; init; } = 20;

    [Setting("Ignore messages older than", Unit = "days", Min = 1, Max = 365)]
    public int MaxAgeDays { get; init; } = 7;

    [Setting("Channels that don't count", Kind = SettingKind.TextChannel)]
    public IReadOnlyList<ulong> ExcludedChannelIds { get; init; } = [];

    [Setting("Emojis that count", Help = "Comma-separated emojis or custom emoji ids. Empty means any.")]
    public IReadOnlyList<string> Emojis { get; init; } = [];
}
