using THOBOTTO.Modules;

namespace THOBOTTO.Wrapped;

public static class WrappedReveal
{
    public const string Anytime = "anytime";
    public const string Year = "year";
    public const string Ended = "ended";
}

// Stored with SettingsStore under the module id.
public sealed record WrappedRules
{
    [Setting("Post Wrapped in", Help = "Where the server's Wrapped is posted. Nothing is posted until it's set.", Kind = SettingKind.TextChannel)]
    public ulong? ChannelId { get; init; }

    [Setting("Monthly recap", Help = "On the 1st of each month: last month's Wrapped.")]
    public bool Monthly { get; init; }

    [Setting("Year in review", Help = "On December 31st: the year's Wrapped, each helper's in its own voice, and a DM with their own to members who asked for one.")]
    public bool Yearly { get; init; } = true;

    [Setting("When Wrapped can be seen", Help = "Kept for a reveal, a month or year can't be looked at with /wrapped, on the panel or in the Activity until it's over. All time is only there when anytime.",
        Choices = [WrappedReveal.Anytime + "=Anytime", WrappedReveal.Year + "=Months anytime; the year from noon on December 31st", WrappedReveal.Ended + "=Once it's over: a month after it ends, the year from noon on December 31st"])]
    public string Reveal { get; init; } = WrappedReveal.Anytime;
}
