using THOBOTTO.Modules;

namespace THOBOTTO.Quotes;

// Stored with SettingsStore under the module id.
public sealed record QuoteRules
{
    [Setting("Quotes channel", Help = "Every saved quote is posted there too, like a hall of fame; nothing until it's set.", Kind = SettingKind.TextChannel)]
    public ulong? ChannelId { get; init; }
}
