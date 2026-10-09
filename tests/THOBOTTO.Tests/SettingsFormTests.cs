using System.Reflection;

using THOBOTTO.Archive;
using THOBOTTO.Events;
using THOBOTTO.Fame;
using THOBOTTO.Modules;
using THOBOTTO.Music;
using THOBOTTO.Panel;
using THOBOTTO.Points;

namespace THOBOTTO.Tests;

public class SettingsFormTests
{
    private static List<(PropertyInfo, SettingAttribute)> Fields<T>() => typeof(T).GetProperties()
        .Where(p => p.GetCustomAttribute<SettingAttribute>() is not null)
        .Select(p => (p, p.GetCustomAttribute<SettingAttribute>()!))
        .ToList();

    private static (Dictionary<string, object?> Values, Dictionary<string, string> Problems) Read<T>(Dictionary<string, string[]> form)
    {
        var (values, problems) = SettingsForm.Read(Fields<T>(), name => form.GetValueOrDefault(name) ?? []);
        return (values.ToDictionary(v => v.Key.Name, v => v.Value), problems);
    }

    [Fact]
    public void Reads_numbers_switches_and_choices()
    {
        var (values, problems) = Read<MusicRules>(new() { ["IdleMinutes"] = ["7"], ["MaxQueue"] = ["300"], ["DefaultSearch"] = ["scsearch"], ["DjOnly"] = ["true"], ["SkipVotePercent"] = ["0"], ["AutoplayBatch"] = ["5"] });
        Assert.Empty(problems);
        Assert.Equal(7, values["IdleMinutes"]);
        Assert.Equal("scsearch", values["DefaultSearch"]);
        Assert.Equal(true, values["DjOnly"]);
        Assert.Equal(0, values["SkipVotePercent"]);
        Assert.Equal(5, values["AutoplayBatch"]);
    }

    [Fact]
    public void An_unticked_switch_is_off()
    {
        var (values, _) = Read<MusicRules>(new() { ["IdleMinutes"] = ["3"], ["MaxQueue"] = ["200"], ["DefaultSearch"] = ["ytsearch"] });
        Assert.Equal(false, values["DjOnly"]);
    }

    [Fact]
    public void Out_of_range_unknown_choices_and_fractions_are_refused()
    {
        var (_, problems) = Read<MusicRules>(new() { ["IdleMinutes"] = ["0"], ["MaxQueue"] = ["2.5"], ["DefaultSearch"] = ["spotify"] });
        Assert.Contains("Between 1 and 120", problems["IdleMinutes"]);
        Assert.Equal("A whole number.", problems["MaxQueue"]);
        Assert.Equal("Pick from the list.", problems["DefaultSearch"]);
    }

    [Fact]
    public void Time_zones_must_exist()
    {
        Assert.Equal("Europe/Copenhagen", Read<EventRules>(new() { ["TimeZone"] = ["Europe/Copenhagen"] }).Values["TimeZone"]);
        Assert.True(Read<EventRules>(new() { ["TimeZone"] = ["Mars/Olympus"] }).Problems.ContainsKey("TimeZone"));
    }

    [Fact]
    public void Empty_clears_what_may_be_empty()
    {
        var (values, problems) = Read<EventRules>(new() { ["TimeZone"] = ["UTC"], ["VoiceCategoryId"] = [""] });
        Assert.Null(values["VoiceCategoryId"]);
        Assert.Equal("Give a number.", problems["ReminderMinutes"]);
    }

    [Fact]
    public void Megabytes_are_stored_as_bytes_and_shown_as_megabytes()
    {
        var (values, _) = Read<ArchiveRules>(new() { ["MaxAttachmentBytes"] = ["25"], ["ExcludedChannelIds"] = ["1", "2"] });
        Assert.Equal(25L * 1_048_576, values["MaxAttachmentBytes"]);
        Assert.Equal(new ulong[] { 1, 2 }, (IEnumerable<ulong>)values["ExcludedChannelIds"]!);

        var property = typeof(ArchiveRules).GetProperty(nameof(ArchiveRules.MaxAttachmentBytes))!;
        Assert.Equal("25", SettingsForm.Text(property, property.GetCustomAttribute<SettingAttribute>()!, new ArchiveRules { MaxAttachmentBytes = 25L * 1_048_576 }));
    }

    [Fact]
    public void Text_lists_are_comma_separated()
    {
        var (values, _) = Read<FameRules>(new() { ["Emojis"] = ["⭐, 🔥 ,⭐"] });
        Assert.Equal(new[] { "⭐", "🔥" }, (IEnumerable<string>)values["Emojis"]!);
    }

    [Fact]
    public void Optional_text_can_be_empty_and_negative_ranges_work()
    {
        var (values, problems) = Read<PointRules>(new() { ["CurrencyName"] = ["coins"], ["CurrencyEmoji"] = [""], ["IdleFloor"] = ["-0.5"] });
        Assert.Null(values["CurrencyEmoji"]);
        Assert.Equal(-0.5, values["IdleFloor"]);
        Assert.False(problems.ContainsKey("IdleFloor"));
    }

    [Fact]
    public void Every_settings_page_has_labels_and_sane_ranges()
    {
        foreach (var type in new[] { typeof(MusicRules), typeof(EventRules), typeof(ArchiveRules), typeof(FameRules), typeof(PointRules) })
        {
            foreach (var property in type.GetProperties().Where(p => p.GetCustomAttribute<SettingAttribute>() is not null))
            {
                var setting = property.GetCustomAttribute<SettingAttribute>()!;
                Assert.False(string.IsNullOrWhiteSpace(setting.Label), property.Name);
                Assert.True(setting.Min <= setting.Max, property.Name);
            }
        }
    }
}
