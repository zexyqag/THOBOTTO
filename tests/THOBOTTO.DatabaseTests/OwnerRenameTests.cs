using THOBOTTO.Mischief;

namespace THOBOTTO.DatabaseTests;

// Renames asked of the owner: in place once their nickname matches, counted from then, and declined ones count for nothing.
[Collection(DatabaseCollection.Name)]
public class OwnerRenameTests(TestDatabase database)
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private const ulong Owner = 7, Ana = 1, Bo = 2;

    [Fact]
    public async Task An_asked_rename_is_in_place_once_the_owner_wears_the_name()
    {
        const ulong guild = 701;
        await using var db = database.CreateDbContext();
        db.Renames.AddRange(
            new() { GuildId = guild, ActorId = Ana, TargetId = Owner, NewName = "Captain", Cost = 50, CreatedAt = Noon },
            new() { GuildId = guild, ActorId = Bo, TargetId = Owner, NewName = "Skipper", Cost = 50, CreatedAt = Noon.AddMinutes(40) });
        await db.SaveChangesAsync();

        Assert.Empty(await OwnerRenames.MarkDoneAsync(db, guild, Owner, "Nobody", Noon.AddHours(1)));
        var done = Assert.Single(await OwnerRenames.MarkDoneAsync(db, guild, Owner, "Skipper", Noon.AddHours(2)));
        Assert.Equal(Bo, done.ActorId);
        Assert.Equal(Noon.AddHours(2), done.DoneAt);

        var history = await RenameHistory.ForTargetAsync(db, guild, Owner, Noon.AddHours(-24));
        Assert.Equal(1, history.RecentCount);
        Assert.Equal(Noon.AddMinutes(40), history.LastAt);
        Assert.Equal(Noon.AddHours(2), history.LastByOthersAt);
    }

    [Fact]
    public async Task A_reset_asked_of_the_owner_is_in_place_once_they_have_no_nickname()
    {
        const ulong guild = 702;
        await using var db = database.CreateDbContext();
        db.Renames.Add(new() { GuildId = guild, ActorId = Ana, TargetId = Owner, NewName = null, Cost = 50, CreatedAt = Noon });
        await db.SaveChangesAsync();

        Assert.Empty(await OwnerRenames.MarkDoneAsync(db, guild, Owner, "Still named", Noon.AddHours(1)));
        Assert.Single(await OwnerRenames.MarkDoneAsync(db, guild, Owner, null, Noon.AddHours(2)));
    }

    [Fact]
    public async Task A_declined_rename_counts_for_nothing()
    {
        const ulong guild = 703;
        await using var db = database.CreateDbContext();
        db.Renames.Add(new() { GuildId = guild, ActorId = Ana, TargetId = Owner, NewName = "Captain", Cost = 50, CreatedAt = Noon, DeclinedAt = Noon.AddDays(1) });
        await db.SaveChangesAsync();

        Assert.Equal(new RenameHistory(0, null, null), await RenameHistory.ForTargetAsync(db, guild, Owner, Noon.AddHours(-24)));
        Assert.Empty(await OwnerRenames.MarkDoneAsync(db, guild, Owner, "Captain", Noon.AddDays(2)));
    }
}
