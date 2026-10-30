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

// The choices as /me calendar offers them.
public enum CalendarChoice
{
    [SlashCommandChoice(Name = "Everything")] Everything,
    [SlashCommandChoice(Name = "Events, and sessions of my games")] Games,
    [SlashCommandChoice(Name = "Only what I'm in for or might come to")] Mine,
    [SlashCommandChoice(Name = "Nothing from this server")] Nothing,
}
