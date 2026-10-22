using THOBOTTO.Sounds;

namespace THOBOTTO.DatabaseTests;

[Collection(DatabaseCollection.Name)]
public class SoundPlaysTests(TestDatabase database)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Plays_are_counted_per_sound_within_the_window_from_both_boards()
    {
        const ulong guild = 901;
        await using var db = database.CreateDbContext();
        db.SoundPlays.AddRange(
            new() { SoundId = 1, GuildId = guild, UserId = 5, At = Now.AddDays(-1) },
            new() { SoundId = 1, GuildId = guild, UserId = 6, At = Now.AddDays(-2), OnDiscord = true },
            new() { SoundId = 2, GuildId = guild, UserId = 5, At = Now.AddDays(-40) },
            new() { SoundId = 3, GuildId = 902, UserId = 5, At = Now.AddDays(-1) });
        await db.SaveChangesAsync();

        var plays = await SoundBoard.PlaysSinceAsync(db, guild, Now.AddDays(-30));
        Assert.Equal(new Dictionary<long, int> { [1] = 2 }, plays);
    }
}
