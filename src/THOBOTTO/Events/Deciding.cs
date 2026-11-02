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

    // Every time has passed: there's nothing left to decide.
    public static bool Lapsed(IReadOnlyList<DateTimeOffset> times, DateTimeOffset now) => times.All(t => t <= now);

    // How the vote says it'll be settled.
    public static string Explain(string rule, int enough, DateTimeOffset closesAt, string planner) => rule switch
    {
        AsSoonAsEnough => $"It's settled as soon as a time has {enough}, else the most votes win <t:{closesAt.ToUnixTimeSeconds()}:R>.",
        PlannerDecides => $"{planner} settles it.",
        _ => $"The most votes win <t:{closesAt.ToUnixTimeSeconds()}:R>.",
    };
}

// The rule as a command option.
public enum Settling
{
    [SlashCommandChoice(Name = "Most votes when the vote closes")] WhenItCloses,
    [SlashCommandChoice(Name = "As soon as a time has enough people")] AsSoonAsEnough,
    [SlashCommandChoice(Name = "Whoever planned it decides")] PlannerDecides,
}
