using System.Text.Json.Nodes;

using THOBOTTO.Backups;

namespace THOBOTTO.Tests;

public class BackupTests
{
    private static JsonObject Settings() => JsonNode.Parse("""
        { "showcaseChannelId": 11, "excludedChannelIds": [12, 13], "voiceCategoryId": 14, "autoModExemptRoleIds": [21], "maxQueue": 200, "logChannelId": null }
        """)!.AsObject();

    [Fact]
    public void Ids_in_settings_are_found_by_their_names()
    {
        var (channels, roles) = BackupIds.Find(Settings());
        Assert.Equal(new ulong[] { 11, 12, 13, 14 }, channels.Order());
        Assert.Equal([21UL], roles);
    }

    [Fact]
    public void Remapping_swaps_ids_and_drops_ones_without_a_counterpart()
    {
        var remapped = BackupIds.Remap(Settings(), id => id == 13 ? null : id + 100, id => id + 1000);
        Assert.Equal(111UL, remapped["showcaseChannelId"]!.GetValue<ulong>());
        Assert.Equal([112UL], remapped["excludedChannelIds"]!.AsArray().Select(n => n!.GetValue<ulong>()));
        Assert.Equal(114UL, remapped["voiceCategoryId"]!.GetValue<ulong>());
        Assert.Equal([1021UL], remapped["autoModExemptRoleIds"]!.AsArray().Select(n => n!.GetValue<ulong>()));
        Assert.Equal(200, remapped["maxQueue"]!.GetValue<int>());
    }

    [Fact]
    public void A_backup_comes_back_the_same_from_its_file()
    {
        var backup = new ServerBackup
        {
            ServerId = "1557576140221059122",
            ServerName = "Bottesting",
            CreatedAt = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero),
            Roles = new() { ["21"] = "Tester" },
            Settings = new() { ["music"] = Settings() },
            Permissions = [new("21", "music.dj")],
        };
        var (back, problem) = ServerBackup.Parse(backup.ToJson());
        Assert.Null(problem);
        Assert.Equal("Tester", back!.Roles["21"]);
        Assert.Equal("music.dj", back.Permissions[0].Permission);
        Assert.Equal(11UL, back.Settings["music"]["showcaseChannelId"]!.GetValue<ulong>());
        Assert.Equal("thobotto-bottesting-2026-10-09.json", back.FileName);
    }

    [Fact]
    public void Other_files_are_refused()
    {
        Assert.NotNull(ServerBackup.Parse("""{"name": "Jeeves", "lines": {}}""").Problem);
        Assert.NotNull(ServerBackup.Parse("not json").Problem);
    }
}
