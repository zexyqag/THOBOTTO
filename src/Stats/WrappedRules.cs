using THOBOTTO.Modules;

namespace THOBOTTO.Stats;

// Stored with SettingsStore under the module id.
public sealed record WrappedRules
{
    [Setting("Post Wrapped in", Help = "Where the server's Wrapped is posted. Nothing is posted until it's set.", Kind = SettingKind.TextChannel)]
    public ulong? ChannelId { get; init; }

    [Setting("Monthly recap", Help = "On the 1st of each month: last month's Wrapped.")]
    public bool Monthly { get; init; }

    [Setting("Year in review", Help = "On December 31st: the year's Wrapped, each helper's in its own voice, and a DM with their own to members who asked for one.")]
    public bool Yearly { get; init; } = true;
}
