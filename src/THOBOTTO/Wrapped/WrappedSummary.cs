using THOBOTTO.Happenings;
using THOBOTTO.Modules;
using THOBOTTO.Overview;

namespace THOBOTTO.Wrapped;

// Where the server's Wrapped goes, as its settings and the happenings forum's stand.
public sealed class WrappedSummary(SettingsStore settings, HappeningForum forum, OverviewBoard overview) : ISettingsSummary
{
    public string ModuleId => WrappedPoster.ModuleId;

    public async Task<string?> NowAsync(ulong guildId, Func<ulong, string> channel)
    {
        var rules = await settings.GetAsync<WrappedRules>(guildId, ModuleId);
        if (!rules.Monthly && !rules.Yearly)
            return "Now: nothing is posted on its own (the monthly recap and the year in review are off); Wrapped can still be looked at with /wrapped, on the panel and in the Activity.";
        List<string> places = [];
        if (await forum.ShowsAsync(guildId, WrappedPoster.Happening.Id) && await forum.ForumOfAsync(guildId) is { } forumId)
            places.Add($"the happenings forum ({channel(forumId)})");
        if (rules.ChannelId is { } own)
            places.Add(channel(own));
        if (places.Count == 0)
            return "Now: Wrapped isn't posted anywhere: give it a channel of its own here, or set up the happenings forum with Wrapped on. It can still be looked at with /wrapped, on the panel and in the Activity.";
        var which = rules.Monthly && rules.Yearly ? "each month's and the year's Wrapped are" : rules.Monthly ? "each month's Wrapped is" : "the year's Wrapped is";
        var widget = await overview.ShowsAsync(guildId, LatestWrappedWidget.KindId) ? " A Latest Wrapped widget shows it for a few days after." : "";
        return $"Now: {which} posted in {string.Join(" and ", places)}.{widget}";
    }
}
