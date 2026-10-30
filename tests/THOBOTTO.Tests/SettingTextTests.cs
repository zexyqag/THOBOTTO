using System.Reflection;

using THOBOTTO.Activity.Watch;
using THOBOTTO.Fame;
using THOBOTTO.Happenings;
using THOBOTTO.Modules;
using THOBOTTO.Panel;

namespace THOBOTTO.Tests;

// /setup set: what's typed becomes what the panel's form would have posted, and is read the same way.
public class SettingTextTests
{
    private static (PropertyInfo Property, SettingAttribute Setting) Field<T>(string name)
        => (typeof(T).GetProperty(name)!, typeof(T).GetProperty(name)!.GetCustomAttribute<SettingAttribute>()!);

    private static object? Read<T>(string name, object current, string typed)
    {
        var field = Field<T>(name);
        var (posted, problem) = SettingText.Posted(null!, field.Property, field.Setting, current, typed);
        Assert.Null(problem);
        var (values, problems) = SettingsForm.Read([field], _ => posted);
        Assert.Empty(problems);
        return values[field.Property];
    }

    [Fact]
    public void A_switch_takes_on_and_off()
    {
        Assert.Equal(false, Read<HappeningRules>(nameof(HappeningRules.Bets), new HappeningRules(), "off"));
        Assert.Equal(true, Read<HappeningRules>(nameof(HappeningRules.Bets), new HappeningRules { Bets = false }, "on"));
        var field = Field<HappeningRules>(nameof(HappeningRules.Bets));
        Assert.NotNull(SettingText.Posted(null!, field.Property, field.Setting, new HappeningRules(), "maybe").Problem);
    }

    [Fact]
    public void A_choice_is_taken_by_its_value_or_its_label()
    {
        Assert.Equal(WatchControl.Djs, Read<WatchRules>(nameof(WatchRules.Control), new WatchRules(), "Those with music.dj"));
        Assert.Equal(WatchControl.Anyone, Read<WatchRules>(nameof(WatchRules.Control), new WatchRules(), "anyone"));
        Assert.Equal("Those with music.dj", SettingText.Show(Field<WatchRules>(nameof(WatchRules.Control)).Property, Field<WatchRules>(nameof(WatchRules.Control)).Setting, new WatchRules { Control = WatchControl.Djs }));
    }

    [Fact]
    public void A_number_out_of_range_is_told()
    {
        var field = Field<WatchRules>(nameof(WatchRules.SkipVotePercent));
        var (posted, _) = SettingText.Posted(null!, field.Property, field.Setting, new WatchRules(), "150");
        Assert.Single(SettingsForm.Read([field], _ => posted).Problems);
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
