using THOBOTTO.Kaomoji;

namespace THOBOTTO.DatabaseTests;

// Someone's favourite faces: counted, the most used first, only the latest kept, forgotten on asking.
[Collection(DatabaseCollection.Name)]
public class KaomojiFavouritesTests(TestDatabase database)
{
    [Fact]
    public async Task Uses_are_counted_and_the_most_used_come_first()
    {
        var favourites = new KaomojiFavourites(database, new Ticking());
        await favourites.UsedAsync(901, "ʕ•ᴥ•ʔ");
        await favourites.UsedAsync(901, "ಠ_ಠ");
        await favourites.UsedAsync(901, "ಠ_ಠ");
        await favourites.UsedAsync(901, "(¬‿¬)");
        await favourites.UsedAsync(902, "(⌐■_■)");

        Assert.Equal(["ಠ_ಠ", "(¬‿¬)", "ʕ•ᴥ•ʔ"], await favourites.TextsAsync(901));
        Assert.Equal(2, (await favourites.OfAsync(901))[0].Uses);
    }

    [Fact]
    public async Task Only_the_latest_are_kept()
    {
        var favourites = new KaomojiFavourites(database, new Ticking());
        await favourites.UsedAsync(903, "first");
        await favourites.UsedAsync(903, "first");
        for (var i = 0; i < KaomojiFavourites.Kept; i++)
            await favourites.UsedAsync(903, $"face {i}");

        var kept = await favourites.TextsAsync(903);
        Assert.Equal(KaomojiFavourites.Kept, kept.Count);
        Assert.DoesNotContain("first", kept);
    }

    [Fact]
    public async Task One_or_all_are_forgotten()
    {
        var favourites = new KaomojiFavourites(database, new Ticking());
        await favourites.UsedAsync(904, "ʕ•ᴥ•ʔ");
        await favourites.UsedAsync(904, "ಠ_ಠ");

        Assert.True(await favourites.ForgetAsync(904, "ʕ•ᴥ•ʔ"));
        Assert.False(await favourites.ForgetAsync(904, "ʕ•ᴥ•ʔ"));
        Assert.Equal(["ಠ_ಠ"], await favourites.TextsAsync(904));
        Assert.True(await favourites.ForgetAsync(904));
        Assert.Empty(await favourites.TextsAsync(904));
    }

    // A minute later at each look, so the order of uses is plain.
    private sealed class Ticking : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now = _now.AddMinutes(1);
    }
}
