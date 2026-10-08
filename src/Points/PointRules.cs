using THOBOTTO.Modules;

namespace THOBOTTO.Points;

// Every member has an activity level: the sum of the components below. Each minute they earn
// BasePerMinute × activity level. Components rise with activity and sink back to zero; a member
// whose components have all faded is idle and gets IdleFloor instead (negative drains).
// Stored with SettingsStore under the module id.
public sealed record PointRules
{
    [Setting("Currency name", Section = "General")]
    public string CurrencyName { get; init; } = "points";

    [Setting("Currency emoji", Section = "General")]
    public string? CurrencyEmoji { get; init; }

    [Setting("Points a minute at full activity", Help = "Earned each minute: this × the activity level.", Section = "General", Min = 0, Max = 100)]
    public double BasePerMinute { get; init; } = 0.2;

    [Setting("While idle", Help = "Earned a minute when all activity has faded; negative drains.", Section = "General", Min = -1, Max = 0)]
    public double IdleFloor { get; init; }

    [Setting("Balances may go below zero", Section = "General")]
    public bool AllowNegativeBalance { get; init; }

    [Setting("Leaderboard is public", Section = "General")]
    public bool LeaderboardPublic { get; init; } = true;

    [Setting("Expire each week", Section = "General", Unit = "%", Min = 0, Max = 100)]
    public double WeeklyExpiryPercent { get; init; }

    [Setting("Most one member gives away a day", Help = "With /points give, in a rolling 24 hours.", Section = "General", Unit = "points", Min = 0, Max = 1_000_000)]
    public double KudosDailyLimit { get; init; } = 100;

    [Setting("Activity from voice", Help = "Reached while in voice with another human (not deafened, not AFK).", Section = "Voice", Min = 0, Max = 10)]
    public double VoiceMax { get; init; } = 1.0;

    [Setting("Rises over", Help = "About 95% of the way after three of these.", Section = "Voice", Unit = "minutes", Min = 1, Max = 1440)]
    public double VoiceRiseMinutes { get; init; } = 20;

    [Setting("Falls over", Section = "Voice", Unit = "minutes", Min = 1, Max = 1440)]
    public double VoiceFallMinutes { get; init; } = 20;

    [Setting("Most activity from chat", Section = "Chat", Min = 0, Max = 10)]
    public double ChatMax { get; init; } = 1.0;

    [Setting("Each message adds", Section = "Chat", Min = 0, Max = 10)]
    public double ChatBump { get; init; } = 0.1;

    [Setting("Plus per character", Section = "Chat", Min = 0, Max = 1)]
    public double ChatBumpPerChar { get; init; } = 0.002;

    [Setting("At most per message", Section = "Chat", Min = 0, Max = 10)]
    public double ChatBumpMax { get; init; } = 0.3;

    [Setting("One message counts every", Section = "Chat", Unit = "seconds", Min = 0, Max = 3600)]
    public int ChatCooldownSeconds { get; init; } = 30;

    [Setting("Halves every", Section = "Chat", Unit = "minutes", Min = 1, Max = 1440)]
    public double ChatHalfLifeMinutes { get; init; } = 15;

    [Setting("Most activity from reactions", Section = "Reactions received", Min = 0, Max = 10)]
    public double ReceivedMax { get; init; } = 0.5;

    [Setting("Each reaction adds", Section = "Reactions received", Min = 0, Max = 10)]
    public double ReceivedBump { get; init; } = 0.05;

    [Setting("Counted per person", Help = "Times one person's reactions count within the window.", Section = "Reactions received", Min = 0, Max = 100)]
    public int ReceivedPerReactor { get; init; } = 3;

    [Setting("Window", Section = "Reactions received", Unit = "minutes", Min = 1, Max = 1440)]
    public int ReceivedWindowMinutes { get; init; } = 10;

    [Setting("Halves every", Section = "Reactions received", Unit = "minutes", Min = 1, Max = 1440)]
    public double ReceivedHalfLifeMinutes { get; init; } = 30;

    [Setting("Most activity from reacting", Help = "Counted once per message.", Section = "Reactions given", Min = 0, Max = 10)]
    public double GivenMax { get; init; } = 0.2;

    [Setting("Each reaction adds", Section = "Reactions given", Min = 0, Max = 10)]
    public double GivenBump { get; init; } = 0.02;

    [Setting("Halves every", Section = "Reactions given", Unit = "minutes", Min = 1, Max = 1440)]
    public double GivenHalfLifeMinutes { get; init; } = 30;

    public string Format(double amount)
        => $"{Math.Floor(amount):0} {(CurrencyEmoji is null ? CurrencyName : $"{CurrencyEmoji} {CurrencyName}")}";
}
