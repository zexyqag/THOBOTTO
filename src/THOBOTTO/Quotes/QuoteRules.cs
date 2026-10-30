using THOBOTTO.Modules;

namespace THOBOTTO.Quotes;

// Stored with SettingsStore under the module id.
public sealed record QuoteRules
{
    [Setting("Post saved quotes", Help = "Every saved quote is posted in the hall of fame's showcase, beside its highlights, or in its own channel below.")]
    public bool PostQuotes { get; init; } = true;

    [Setting("Quotes in their own channel", Help = "None: in the hall of fame's showcase.", Kind = SettingKind.TextChannel)]
    public ulong? ChannelId { get; init; }

    // Where saved quotes are posted, or null.
    public ulong? PostsTo(ulong? showcase) => PostQuotes ? ChannelId ?? showcase : null;
}
