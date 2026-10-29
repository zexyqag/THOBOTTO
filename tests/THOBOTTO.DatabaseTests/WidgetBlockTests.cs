using THOBOTTO.Sounds;

namespace THOBOTTO.DatabaseTests;

[Collection(DatabaseCollection.Name)]
public class WidgetBlockTests(TestDatabase database)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private sealed class At(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // Only the library's sounds, most played in the window first.
    [Fact]
    public async Task The_sounds_block_ranks_the_librarys_sounds_by_recent_plays()
    {
        const ulong guild = 903;
        await using var db = database.CreateDbContext();
        Sound Sound(string name, string state) => new() { GuildId = guild, Name = name, FileName = name + ".mp3", State = state };
        var (ding, horn, gone) = (Sound("ding", SoundStates.Library), Sound("horn", SoundStates.Library), Sound("gone", SoundStates.Removed));
        db.Sounds.AddRange(ding, horn, gone);
        await db.SaveChangesAsync();
        db.SoundPlays.AddRange(
            new() { SoundId = ding.Id, GuildId = guild, UserId = 5, At = Now.AddDays(-1) },
            new() { SoundId = horn.Id, GuildId = guild, UserId = 5, At = Now.AddDays(-1) },
            new() { SoundId = horn.Id, GuildId = guild, UserId = 6, At = Now.AddDays(-2) },
            new() { SoundId = ding.Id, GuildId = guild, UserId = 5, At = Now.AddDays(-20) },
            new() { SoundId = gone.Id, GuildId = guild, UserId = 5, At = Now.AddDays(-1) });
        await db.SaveChangesAsync();

        var block = Assert.Single(await new TopSoundsWidget(database, new At(Now)).RenderAsync(guild, new Dictionary<string, string> { ["days"] = "7" }));
        Assert.Equal("Most played sounds, 7 days", block.Title);
        Assert.Equal("1. 🔊 **horn** · 2 plays\n2. 🔊 **ding** · 1 play", block.Text);
    }
}
