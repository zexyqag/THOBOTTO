using Microsoft.EntityFrameworkCore;

using THOBOTTO.Sounds;

namespace THOBOTTO.DatabaseTests;

[Collection(DatabaseCollection.Name)]
public class SoundCleanupTests(TestDatabase database)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static Sound Row(ulong guildId, string name, string state, DateTimeOffset? decidedAt, DateTimeOffset? removedAt) => new()
    {
        GuildId = guildId,
        Name = name,
        CreatorId = 1,
        ProposerId = 1,
        Audio = [1, 2, 3],
        FileName = name + ".mp3",
        Milliseconds = 1000,
        State = state,
        ProposedAt = Now.AddDays(-60),
        VoteEndsAt = Now.AddDays(-57),
        DecidedAt = decidedAt,
        RemovedAt = removedAt,
    };

    [Fact]
    public async Task Old_removed_and_rejected_sounds_lose_their_audio_the_rest_keep_it()
    {
        const ulong guild = 801;
        await using var db = database.CreateDbContext();
        db.Sounds.AddRange(
            Row(guild, "old-rejected", SoundStates.Rejected, Now.AddDays(-40), null),
            Row(guild, "old-removed", SoundStates.Removed, Now.AddDays(-50), Now.AddDays(-31)),
            Row(guild, "fresh-removed", SoundStates.Removed, Now.AddDays(-50), Now.AddDays(-10)),
            Row(guild, "kept", SoundStates.Library, Now.AddDays(-50), null),
            Row(802, "elsewhere", SoundStates.Rejected, Now.AddDays(-40), null));
        await db.SaveChangesAsync();

        Assert.Equal(2, await SoundBoard.DropAudioAsync(db, guild, Now.AddDays(-30)));

        var audio = await db.Sounds.AsNoTracking().Where(s => s.GuildId == guild || s.GuildId == 802).ToDictionaryAsync(s => s.Name, s => s.Audio != null);
        Assert.Equal(new Dictionary<string, bool> { ["old-rejected"] = false, ["old-removed"] = false, ["fresh-removed"] = true, ["kept"] = true, ["elsewhere"] = true }, audio);
    }
}
