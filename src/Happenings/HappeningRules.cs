using THOBOTTO.Modules;

namespace THOBOTTO.Happenings;

// Stored with SettingsStore under the module id.
public sealed record HappeningRules
{
    [Setting("Forum", Help = "Each happening gets a post of its own there, beside its card where it was started; none, only where it was started. The bot needs Manage Threads there (to tag and close posts) and Manage Channel (to add the tags it uses).", Kind = SettingKind.ForumChannel)]
    public ulong? ForumChannelId { get; init; }

    [Setting("Events", Section = "In the forum")]
    public bool Events { get; init; } = true;

    [Setting("Game sessions", Section = "In the forum")]
    public bool Sessions { get; init; } = true;

    [Setting("Bets", Section = "In the forum")]
    public bool Bets { get; init; } = true;

    [Setting("Emoji and sticker proposals", Section = "In the forum")]
    public bool Emoji { get; init; } = true;

    [Setting("Sound proposals", Section = "In the forum")]
    public bool Sounds { get; init; } = true;

    public bool Shows(string kind) => kind switch
    {
        HappeningKinds.Event => Events,
        HappeningKinds.Session => Sessions,
        HappeningKinds.Bet => Bets,
        HappeningKinds.Emoji => Emoji,
        HappeningKinds.Sound => Sounds,
        _ => false,
    };
}
