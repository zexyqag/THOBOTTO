using THOBOTTO.Modules;

namespace THOBOTTO.Quotes;

// Stored with SettingsStore under the module id.
public sealed record QuoteRules
{
    [Setting("Post saved quotes", Help = "Every saved quote is posted in the hall of fame's showcase (set on its page), beside its highlights, unless quotes have a channel of their own below.")]
    public bool PostQuotes { get; init; } = true;

    [Setting("Quotes in their own channel", Help = "Optional. None: in the hall of fame's showcase.", Kind = SettingKind.TextChannel)]
    public ulong? ChannelId { get; init; }

    // Where saved quotes are posted, or null.
    public ulong? PostsTo(ulong? showcase) => PostQuotes ? ChannelId ?? showcase : null;
}
