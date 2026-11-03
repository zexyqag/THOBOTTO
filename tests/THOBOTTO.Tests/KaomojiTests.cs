using THOBOTTO.Kaomoji;

namespace THOBOTTO.Tests;

public class KaomojiTests
{
    private static readonly IReadOnlyList<Face> All = KaomojiLibrary.All;

    private static string First(string search) => KaomojiLibrary.Search(All, search).First().Text;

    [Fact]
    public void The_library_loads_each_face_once()
    {
        Assert.InRange(All.Count, 8000, 10000);
        Assert.Equal(All.Count, All.Select(f => f.Text).Distinct().Count());
        Assert.Equal(All.Count, All.Select(f => f.Key).Distinct().Count());
        Assert.All(All, f => Assert.False(string.IsNullOrWhiteSpace(f.Name)));
    }

    [Fact]
    public void Classics_are_found_by_the_names_people_know()
    {
        Assert.Equal("( ͡° ͜ʖ ͡°)", First("lenny"));
        Assert.Equal("ಠ_ಠ", First("disapproval"));
        Assert.Equal("(╯°□°)╯︵ ┻━┻", First("table flip"));
        Assert.Equal("┬─┬ノ( º _ ºノ)", First("unflip"));
        Assert.Equal("¯\\_(ツ)_/¯", First("shrug"));
        Assert.Equal("ʕノ•ᴥ•ʔノ ︵ ┻━┻", First("bear table flip"));
    }

    [Fact]
    public void Nothing_typed_offers_the_classics_first()
        => Assert.Equal(["¯\\_(ツ)_/¯", "( ͡° ͜ʖ ͡°)", "ಠ_ಠ"], KaomojiLibrary.Search(All, " ").Take(3).Select(f => f.Text));

    [Fact]
    public void Every_word_must_match_and_a_start_of_a_word_does()
    {
        Assert.Empty(KaomojiLibrary.Search(All, "bear zzqx"));
        Assert.Contains(KaomojiLibrary.Search(All, "tabl"), f => f.Text == "(╯°□°)╯︵ ┻━┻");
    }

    [Fact]
    public void A_pick_is_its_key_else_the_best_for_what_was_typed()
    {
        var own = new Face("s7", "(=ↀωↀ=)", "Server cat", ["cat"]);
        IReadOnlyList<Face> faces = [own, .. All];

        Assert.Same(own, KaomojiLibrary.Pick(faces, "s7", []));
        Assert.Same(own, KaomojiLibrary.Pick(faces, "server cat", []));
        Assert.Equal("ಠ_ಠ", KaomojiLibrary.Pick(faces, "ಠ_ಠ", [])!.Text);
        Assert.Null(KaomojiLibrary.Pick(faces, "zzqx", []));
    }

    [Fact]
    public void Favourites_come_first_most_used_first_then_the_rest_as_found()
    {
        IReadOnlyList<string> mine = ["ʕ•ᴥ•ʔ", "ಠ_ಠ"];

        Assert.Equal(["ʕ•ᴥ•ʔ", "ಠ_ಠ", "¯\\_(ツ)_/¯", "( ͡° ͜ʖ ͡°)"], KaomojiLibrary.Favoured(KaomojiLibrary.Search(All, ""), mine).Take(4).Select(f => f.Text));
        var tables = KaomojiLibrary.Search(All, "table flip").ToList();
        Assert.Equal(tables.Select(f => f.Text), KaomojiLibrary.Favoured(tables, mine).Select(f => f.Text));
    }

    [Fact]
    public void A_favourite_found_by_what_was_typed_is_picked_over_the_best_match()
    {
        Assert.Equal("(╯°□°)╯︵ ┻━┻", KaomojiLibrary.Pick(All, "table flip", [])!.Text);
        Assert.Equal("ʕノ•ᴥ•ʔノ ︵ ┻━┻", KaomojiLibrary.Pick(All, "table flip", ["ʕノ•ᴥ•ʔノ ︵ ┻━┻"])!.Text);
    }

    [Fact]
    public void A_face_both_a_server_and_the_library_have_is_offered_once()
    {
        IReadOnlyList<Face> faces = [new("s3", "ʕ•ᴥ•ʔ", "Our bear", ["bear"]), .. All];

        Assert.Single(KaomojiLibrary.Favoured(KaomojiLibrary.Search(faces, "bear"), ["ʕ•ᴥ•ʔ"]), f => f.Text == "ʕ•ᴥ•ʔ");
        Assert.Equal("s3", KaomojiLibrary.Favoured(KaomojiLibrary.Search(faces, "bear"), ["ʕ•ᴥ•ʔ"]).First().Key);
    }

    [Fact]
    public void Markdown_in_a_face_is_escaped()
    {
        Assert.Equal("¯\\\\\\_(ツ)\\_/¯", KaomojiLibrary.Escaped("¯\\_(ツ)_/¯"));
        Assert.Equal("\\*\\~\\`\\|\\>\\#\\-\\[\\]\\<", KaomojiLibrary.Escaped("*~`|>#-[]<"));
        Assert.Equal("(◕‿◕)", KaomojiLibrary.Escaped("(◕‿◕)"));
    }
}
