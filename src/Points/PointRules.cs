namespace THOBOTTO.Points;

// Every member has an activity level: the sum of the components below. Each minute they earn
// BasePerMinute × activity level. Components rise with activity and sink back to zero; a member
// whose components have all faded is idle and gets IdleFloor instead (negative drains).
// Stored with SettingsStore under the module id.
public sealed record PointRules
{
    public string CurrencyName { get; init; } = "points";
    public string? CurrencyEmoji { get; init; }
    public double BasePerMinute { get; init; } = 0.2;
    public double IdleFloor { get; init; }
    public bool AllowNegativeBalance { get; init; }
    public bool LeaderboardPublic { get; init; } = true;
    public double WeeklyExpiryPercent { get; init; }

    // Most points one member can give away with /kudos in a rolling 24 hours.
    public double KudosDailyLimit { get; init; } = 100;

    // Voice: approaches VoiceMax while in voice with another human (not deafened, not AFK).
    // Time constants: about 95% of the way after three of them.
    public double VoiceMax { get; init; } = 1.0;
    public double VoiceRiseMinutes { get; init; } = 20;
    public double VoiceFallMinutes { get; init; } = 20;

    // Chat: each message (one per cooldown) bumps by ChatBump + length × ChatBumpPerChar, at most ChatBumpMax.
    public double ChatMax { get; init; } = 1.0;
    public double ChatBump { get; init; } = 0.1;
    public double ChatBumpPerChar { get; init; } = 0.002;
    public double ChatBumpMax { get; init; } = 0.3;
    public int ChatCooldownSeconds { get; init; } = 30;
    public double ChatHalfLifeMinutes { get; init; } = 15;

    // Reactions received: counted up to ReceivedPerReactor times per reactor per window.
    public double ReceivedMax { get; init; } = 0.5;
    public double ReceivedBump { get; init; } = 0.05;
    public int ReceivedPerReactor { get; init; } = 3;
    public int ReceivedWindowMinutes { get; init; } = 10;
    public double ReceivedHalfLifeMinutes { get; init; } = 30;

    // Reactions given: counted once per message.
    public double GivenMax { get; init; } = 0.2;
    public double GivenBump { get; init; } = 0.02;
    public double GivenHalfLifeMinutes { get; init; } = 30;

    public string Format(double amount)
        => $"{Math.Floor(amount):0} {(CurrencyEmoji is null ? CurrencyName : $"{CurrencyEmoji} {CurrencyName}")}";
}
