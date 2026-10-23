using THOBOTTO.Helpers;

namespace THOBOTTO.DatabaseTests;

// The personalities that would rather speak through a particular engine, for the status board.
[Collection(DatabaseCollection.Name)]
public class PersonalityPreferenceTests(TestDatabase database)
{
    [Fact]
    public async Task Those_with_a_preference_are_listed_by_name_across_servers()
    {
        await using var db = database.CreateDbContext();
        db.Personalities.AddRange(
            new() { GuildId = 801, Name = "Zorp", Engine = "kokoro", CreatedAt = DateTimeOffset.UnixEpoch },
            new() { GuildId = 802, Name = "Jeeves", CreatedAt = DateTimeOffset.UnixEpoch },
            new() { GuildId = 802, Name = "Unit 404", Engine = "espeak", CreatedAt = DateTimeOffset.UnixEpoch });
        await db.SaveChangesAsync();

        var listed = (await PersonalityBook.PreferringAsync(db)).Where(p => p.GuildId is 801 or 802).ToList();
        Assert.Equal([(802UL, "Unit 404", "espeak"), (801UL, "Zorp", "kokoro")], listed);
    }
}
