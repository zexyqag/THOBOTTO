using THOBOTTO.Discord;
using THOBOTTO.Sounds;

namespace THOBOTTO.Tests;

public class SoundSyncTests
{
    private static Sound Row(long id, string name, string state, ulong? discordId = null, int ms = 3000)
        => new() { Id = id, GuildId = 1, Name = name, FileName = name + ".mp3", Milliseconds = ms, State = state, DiscordId = discordId };

    [Fact]
    public void In_step_means_nothing_to_do()
    {
        var plan = SoundSync.Plan([new(10, "boom", 5)], [Row(1, "boom", SoundStates.Library, 10)]);
        Assert.Empty(plan.TakenOff);
        Assert.Empty(plan.Unknown);
        Assert.Empty(plan.Renames);
    }

    [Fact]
    public void A_linked_sound_gone_on_discord_is_taken_off_and_a_new_one_there_is_unknown()
    {
        var plan = SoundSync.Plan([new(11, "airhorn", null)], [Row(1, "boom", SoundStates.Library, 10)]);
        Assert.Equal([1L], plan.TakenOff);
        Assert.Equal([11UL], plan.Unknown.Select(u => u.Id));
    }

    [Fact]
    public void A_sound_removed_from_the_library_keeps_its_link_and_isnt_imported_again()
    {
        var plan = SoundSync.Plan([new(10, "boom", 5)], [Row(1, "boom", SoundStates.Removed, 10)]);
        Assert.Empty(plan.Unknown);
        Assert.Empty(plan.TakenOff);
    }

    [Fact]
    public void A_name_changed_on_discord_follows_unless_taken()
    {
        var rows = new[] { Row(1, "boom", SoundStates.Library, 10), Row(2, "bang", SoundStates.Voting), Row(3, "clap", SoundStates.Library, 12) };
        var plan = SoundSync.Plan([new(10, "Bang", 5), new(12, "Clap", 5)], rows);
        Assert.Equal([(3L, "Clap")], plan.Renames);
    }

    [Fact]
    public void A_name_the_library_cant_use_doesnt_follow()
        => Assert.Empty(SoundSync.Plan([new(10, "boom!!", 5)], [Row(1, "boom", SoundStates.Library, 10)]).Renames);

    [Fact]
    public void A_discord_sound_matches_an_unlinked_sound_of_the_same_name_and_length()
    {
        var rows = new[] { Row(1, "Boom", SoundStates.Library, ms: 3100), Row(2, "clap", SoundStates.Library, 20), Row(3, "bang", SoundStates.Library, ms: 5000) };
        Assert.Equal(1, SoundSync.Match("boom", 3000, rows)?.Id);
        Assert.Null(SoundSync.Match("clap", 3000, rows));
        Assert.Null(SoundSync.Match("bang", 3000, rows));
    }

    [Fact]
    public void The_report_says_what_changed()
    {
        Assert.Equal("3 of 8 Discord soundboard slots used; in step.", new SyncReport(3, 8, 0, 0, 0, 0, false, DateTimeOffset.UnixEpoch).Describe());
        Assert.Equal("3 of 8 Discord soundboard slots used; 1 imported, 2 taken off there, kept here.", new SyncReport(3, 8, 1, 0, 0, 2, false, DateTimeOffset.UnixEpoch).Describe());
        Assert.StartsWith("Discord didn't list", new SyncReport(0, 0, 0, 0, 0, 0, true, DateTimeOffset.UnixEpoch).Describe());
    }
}
