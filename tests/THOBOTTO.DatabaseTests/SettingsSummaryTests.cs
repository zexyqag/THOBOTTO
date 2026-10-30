using THOBOTTO.Fame;
using THOBOTTO.Gate;
using THOBOTTO.Moderation;
using THOBOTTO.Modules;
using THOBOTTO.Quotes;

namespace THOBOTTO.DatabaseTests;

// The line at the top of a module's settings: where things go now, read with the other modules' settings.
[Collection(DatabaseCollection.Name)]
public class SettingsSummaryTests(TestDatabase database)
{
    private static string Channel(ulong id) => $"#{id}";

    [Fact]
    public async Task Saved_quotes_go_to_the_showcase_unless_they_have_their_own_channel()
    {
        const ulong guild = 905;
        var settings = new SettingsStore(database, TimeProvider.System);
        var quotes = new QuotesSummary(settings);
        Assert.Equal("Now: saved quotes aren't posted: they have no channel of their own, and the hall of fame has no showcase set.", await quotes.NowAsync(guild, Channel));
        await settings.SetAsync(guild, HallOfFame.ModuleId, new FameRules { ShowcaseChannelId = 50 }, 1, "test");
        Assert.Equal("Now: saved quotes are posted in the hall of fame's showcase, #50.", await quotes.NowAsync(guild, Channel));
        Assert.Equal("Now: highlights are posted in #50.", await new FameSummary(settings).NowAsync(guild, Channel));
        await settings.SetAsync(guild, QuoteCommands.ModuleId, new QuoteRules { ChannelId = 51 }, 1, "test");
        Assert.Equal("Now: saved quotes are posted in #51, their own channel.", await quotes.NowAsync(guild, Channel));
    }

    [Fact]
    public async Task Cases_appeals_and_the_gates_alerts_go_to_the_moderation_log_unless_set_apart()
    {
        const ulong guild = 906;
        var settings = new SettingsStore(database, TimeProvider.System);
        Assert.Equal("Now: the gate's alerts aren't posted: they have no channel of their own, and there's no moderation log.", await new GateSummary(settings).NowAsync(guild, Channel));
        await settings.SetAsync(guild, CaseBook.ModuleId, new ModRules { LogChannelId = 60, TakeAppeals = true }, 1, "test");
        Assert.Equal("Now: every case is posted in #60; ban appeals go there too.", await new ModerationSummary(settings).NowAsync(guild, Channel));
        Assert.Equal("Now: the gate's alerts (raids, newcomers waiting) go to the moderation log, #60.", await new GateSummary(settings).NowAsync(guild, Channel));
        await settings.SetAsync(guild, CaseBook.ModuleId, new ModRules { LogChannelId = 60, TakeAppeals = true, AppealsChannelId = 61 }, 1, "test");
        Assert.Equal("Now: every case is posted in #60; ban appeals go to #61.", await new ModerationSummary(settings).NowAsync(guild, Channel));
    }
}
