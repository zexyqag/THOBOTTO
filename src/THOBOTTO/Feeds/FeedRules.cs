using THOBOTTO.Modules;

namespace THOBOTTO.Feeds;

// Stored with SettingsStore under the module id.
public sealed record FeedRules
{
    [Setting("Check every", Help = "How often each feed is read for new entries.", Unit = "minutes", Min = 5, Max = 1440)]
    public int CheckMinutes { get; init; } = 15;
}
