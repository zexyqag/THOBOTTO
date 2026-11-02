using NetCord.Services.ApplicationCommands;

namespace THOBOTTO.Events;

// How a vote on times is settled, for server votes and group DM proposals alike. Whoever planned it (or, in a server,
// someone with events.manage) can always settle it early.
public static class Deciding
{
    public const string WhenItCloses = "closes";
    public const string AsSoonAsEnough = "enough";
    public const string PlannerDecides = "planner";

    public static readonly string[] Choices =
    [
        WhenItCloses + "=Most votes when the vote closes",
        AsSoonAsEnough + "=As soon as a time has enough people (else most votes when it closes)",
        PlannerDecides + "=Whoever planned it decides",
    ];

    public static string Of(Settling settling) => settling switch
    {
        Settling.AsSoonAsEnough => AsSoonAsEnough,
        Settling.PlannerDecides => PlannerDecides,
        _ => WhenItCloses,
    };

    // The time to go with now, by its index in times (counts: who can make each), or null to keep waiting. Enough: how
    // many it takes under AsSoonAsEnough. Closed: the vote's deadline has passed. Ties go to the earliest; times
    // already past never count, nor does a time nobody can make.
    public static int? Settled(string rule, IReadOnlyList<DateTimeOffset> times, IReadOnlyList<int> counts, int enough, DateTimeOffset now, bool closed)
    {
        var ahead = Enumerable.Range(0, times.Count).Where(i => times[i] > now && counts[i] > 0).ToList();
        if (rule == AsSoonAsEnough && ahead.Where(i => counts[i] >= enough).OrderBy(i => times[i]).Select(i => (int?)i).FirstOrDefault() is { } first)
            return first;
        if (rule == PlannerDecides || !closed)
            return null;
        return MostVotes(times, counts, now);
    }

    public static int? MostVotes(IReadOnlyList<DateTimeOffset> times, IReadOnlyList<int> counts, DateTimeOffset now)
        => Enumerable.Range(0, times.Count).Where(i => times[i] > now && counts[i] > 0)
            .OrderByDescending(i => counts[i]).ThenBy(i => times[i])
            .Select(i => (int?)i).FirstOrDefault();

    // Of several games (by their order), the one with most votes, the first of a tie; null for none.
    public static int? Favourite(IReadOnlyList<int> counts)
        => counts.Count == 0 ? null : Enumerable.Range(0, counts.Count).OrderByDescending(i => counts[i]).First();

    // Every time has passed: there's nothing left to decide.
    public static bool Lapsed(IReadOnlyList<DateTimeOffset> times, DateTimeOffset now) => times.All(t => t <= now);

    // How a vote on the game says it'll be settled, when the time is set already (due: the reminder, else the start).
    public static string ExplainGame(string rule, int enough, DateTimeOffset due, string planner) => rule switch
    {
        AsSoonAsEnough => $"It's settled as soon as a game has {enough}, else by the most votes <t:{due.ToUnixTimeSeconds()}:R>.",
        PlannerDecides => $"{planner} settles it, else the most votes win at the start.",
        _ => $"The most votes win <t:{due.ToUnixTimeSeconds()}:R>.",
    };

    // How the vote says it'll be settled.
    public static string Explain(string rule, int enough, DateTimeOffset closesAt, string planner) => rule switch
    {
        AsSoonAsEnough => $"It's settled as soon as a time has {enough}, else the most votes win <t:{closesAt.ToUnixTimeSeconds()}:R>.",
        PlannerDecides => $"{planner} settles it.",
        _ => $"The most votes win <t:{closesAt.ToUnixTimeSeconds()}:R>.",
    };
}

// How an event whose time is set moves to a time someone suggests; its planner (or events.manage) can always move it.
public static class Moving
{
    public const string Majority = "majority";
    public const string PlannerMoves = "planner";

    // Most of those who are in, and at least one of them: an event nobody's in yet doesn't move by itself.
    public static bool Agreed(int inCount, int agreeingIn) => agreeingIn > 0 && agreeingIn * 2 > inCount;

    public static int Needed(int inCount) => inCount / 2 + 1;

    public static string Of(Moves moves) => moves == Moves.PlannerMoves ? PlannerMoves : Majority;
}

// What people may suggest for a plan, while it's voted on (another option) and once it's set (a change, which goes
// through by Moving): times and, for sessions, games; times only; or nothing.
public static class Suggesting
{
    public const string TimesAndGames = "all";
    public const string Times = "times";
    public const string Nothing = "none";

    public static bool TimesAllowed(string suggestions) => suggestions != Nothing;

    public static bool GamesAllowed(string suggestions) => suggestions == TimesAndGames;

    public static string Of(Suggestable suggestable) => suggestable switch
    {
        Suggestable.Times => Times,
        Suggestable.Nothing => Nothing,
        _ => TimesAndGames,
    };
}

public enum Suggestable
{
    [SlashCommandChoice(Name = "Times and games")] TimesAndGames,
    [SlashCommandChoice(Name = "Times only")] Times,
    [SlashCommandChoice(Name = "Nothing")] Nothing,
}

// The rule as a command option.
public enum Settling
{
    [SlashCommandChoice(Name = "The most votes when the vote closes")] WhenItCloses,
    [SlashCommandChoice(Name = "As soon as enough people can make it")] AsSoonAsEnough,
    [SlashCommandChoice(Name = "Me")] PlannerDecides,
}

public enum Moves
{
    [SlashCommandChoice(Name = "When most of those who are in say it works")] Majority,
    [SlashCommandChoice(Name = "Only when I move it")] PlannerMoves,
}
