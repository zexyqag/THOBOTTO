using NetCord.Services.ApplicationCommands;

namespace THOBOTTO.Events;

// A calendar link: a server's (all its events) or a member's (what they chose, across their servers). The token is the
// link's secret, as calendar apps can't sign in; a new one makes the old link stop working.
public sealed class CalendarLink
{
    public long Id { get; init; }

    public required string Token { get; set; }

    // The server's link; null for a member's.
    public ulong? GuildId { get; init; }

    // The member's link; null for a server's.
    public ulong? UserId { get; init; }

    // A member's choice per server (CalendarChoices), as JSON by the server's id; a server not in it: everything.
    public string Choices { get; set; } = "{}";

    // A member's alarm this long before the events they're in for; none: no alarm.
    public int? ReminderMinutes { get; set; } = 30;

    public DateTimeOffset CreatedAt { get; init; }
}

public static class CalendarChoices
{
    public const string Everything = "all";

    // Events, and sessions of the games whose role the member has.
    public const string Games = "games";

    // Only what the member said they're in for, or might come to.
    public const string Mine = "mine";

    public const string Nothing = "none";

    public static readonly IReadOnlyList<(string Value, string Label)> All =
    [
        (Everything, "Everything"),
        (Games, "Events, and sessions of my games"),
        (Mine, "Only what I'm in for or might come to"),
        (Nothing, "Nothing from this server"),
    ];
}

public static class CalendarReminders
{
    public static readonly IReadOnlyList<(int? Minutes, string Label)> All =
    [
        (null, "No reminders"),
        (10, "10 minutes before"),
        (30, "30 minutes before"),
        (60, "An hour before"),
        (1440, "A day before"),
    ];
}

// The choices as /me calendar offers them.
public enum CalendarChoice
{
    [SlashCommandChoice(Name = "Everything")] Everything,
    [SlashCommandChoice(Name = "Events, and sessions of my games")] Games,
    [SlashCommandChoice(Name = "Only what I'm in for or might come to")] Mine,
    [SlashCommandChoice(Name = "Nothing from this server")] Nothing,
}

public enum CalendarReminder
{
    [SlashCommandChoice(Name = "No reminders")] Off,
    [SlashCommandChoice(Name = "10 minutes before")] TenMinutes,
    [SlashCommandChoice(Name = "30 minutes before")] HalfAnHour,
    [SlashCommandChoice(Name = "An hour before")] Hour,
    [SlashCommandChoice(Name = "A day before")] Day,
}
