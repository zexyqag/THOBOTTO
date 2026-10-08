namespace THOBOTTO.Fame;

// Stored with SettingsStore under the module id.
public sealed record FameRules
{
    // Nothing is tracked until a showcase channel is set.
    public ulong? ShowcaseChannelId { get; init; }

    // Different people (not the author, not bots) who reacted.
    public int Threshold { get; init; } = 5;

    public double Bonus { get; init; } = 20;

    // Reactions on older messages don't count.
    public int MaxAgeDays { get; init; } = 7;

    public IReadOnlyList<ulong> ExcludedChannelIds { get; init; } = [];

    // Emojis that count: unicode characters or custom emoji ids. Empty means any.
    public IReadOnlyList<string> Emojis { get; init; } = [];
}
