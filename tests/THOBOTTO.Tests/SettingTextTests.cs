using System.Reflection;

using THOBOTTO.Commands;
using THOBOTTO.Fame;
using THOBOTTO.Happenings;
using THOBOTTO.Modules;
using THOBOTTO.Panel;
using THOBOTTO.Quotes;
using THOBOTTO.Watch;

namespace THOBOTTO.Tests;

// /setup set: what's typed becomes what the panel's form would have posted, and is read the same way.
public class SettingTextTests
{
    private static (PropertyInfo Property, SettingAttribute Setting) Field<T>(string name)
        => (typeof(T).GetProperty(name)!, typeof(T).GetProperty(name)!.GetCustomAttribute<SettingAttribute>()!);

    // The forum's kinds, as the settings page gives them.
    private static readonly IReadOnlyList<(string Value, string Label)> Kinds = [("bet", "Bets"), ("event", "Events"), ("poll", "Poll results")];

    private static object? Read<T>(string name, object current, string typed, IReadOnlyList<(string Value, string Label)>? switches = null)
    {
        var field = Field<T>(name);
        var (posted, problem) = SettingText.Posted(null!, field.Property, field.Setting, current, typed, switches ?? []);
        Assert.Null(problem);
        var (values, problems) = SettingsForm.Read([field], _ => posted, _ => switches ?? []);
        Assert.Empty(problems);
        return values[field.Property];
    }

    [Fact]
    public void A_switch_takes_on_and_off()
    {
        Assert.Equal(false, Read<QuoteRules>(nameof(QuoteRules.PostQuotes), new QuoteRules(), "off"));
        Assert.Equal(true, Read<QuoteRules>(nameof(QuoteRules.PostQuotes), new QuoteRules { PostQuotes = false }, "on"));
        var field = Field<QuoteRules>(nameof(QuoteRules.PostQuotes));
        Assert.NotNull(SettingText.Posted(null!, field.Property, field.Setting, new QuoteRules(), "maybe", []).Problem);
    }

    // Stored as the ones switched off: +x turns one on, -x off, by its value or label; else the list is the ones on.
    [Fact]
    public void Switches_turn_on_and_off_by_name()
    {
        var current = new HappeningRules { Off = ["poll"] };
        Assert.Equal(new[] { "bet", "poll" }, Read<HappeningRules>(nameof(HappeningRules.Off), current, "-Bets", Kinds));
        Assert.Equal(Array.Empty<string>(), Read<HappeningRules>(nameof(HappeningRules.Off), current, "+poll", Kinds));
        Assert.Equal(new[] { "event", "poll" }, Read<HappeningRules>(nameof(HappeningRules.Off), current, "bets", Kinds));
        Assert.Equal(new[] { "bet", "event", "poll" }, Read<HappeningRules>(nameof(HappeningRules.Off), current, "none", Kinds));
        var field = Field<HappeningRules>(nameof(HappeningRules.Off));
        Assert.Equal("one of: Bets, Events, Poll results.", SettingText.Posted(null!, field.Property, field.Setting, current, "+quiz", Kinds).Problem);
        Assert.Equal("on: Bets, Events; off: Poll results", SettingText.Show(field.Property, field.Setting, current, Kinds));
        Assert.Equal("all on", SettingText.Show(field.Property, field.Setting, new HappeningRules(), Kinds));
    }

    [Fact]
    public void A_choice_is_taken_by_its_value_or_its_label()
    {
        Assert.Equal(WatchControl.Djs, Read<WatchRules>(nameof(WatchRules.Control), new WatchRules(), "Those with music.dj"));
        Assert.Equal(WatchControl.Anyone, Read<WatchRules>(nameof(WatchRules.Control), new WatchRules(), "anyone"));
        Assert.Equal("Those with music.dj", SettingText.Show(Field<WatchRules>(nameof(WatchRules.Control)).Property, Field<WatchRules>(nameof(WatchRules.Control)).Setting, new WatchRules { Control = WatchControl.Djs }, []));
    }

    [Fact]
    public void A_number_out_of_range_is_told()
    {
        var field = Field<WatchRules>(nameof(WatchRules.SkipVotePercent));
        var (posted, _) = SettingText.Posted(null!, field.Property, field.Setting, new WatchRules(), "150", []);
        Assert.Single(SettingsForm.Read([field], _ => posted, _ => []).Problems);
    }

    [Fact]
    public void A_list_takes_a_whole_list_or_changes()
    {
        var current = new FameRules { Emojis = ["⭐", "🔥"] };
        Assert.Equal(new[] { "⭐", "🔥", "😂" }, Read<FameRules>(nameof(FameRules.Emojis), current, "+😂"));
        Assert.Equal(new[] { "🔥" }, Read<FameRules>(nameof(FameRules.Emojis), current, "-⭐"));
        Assert.Equal(new[] { "👍", "👏" }, Read<FameRules>(nameof(FameRules.Emojis), current, "👍, 👏"));
        Assert.Empty((IEnumerable<string>)Read<FameRules>(nameof(FameRules.Emojis), current, "none")!);
    }
}
