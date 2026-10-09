using THOBOTTO.Lastfm;

namespace THOBOTTO.Tests;

public class BlenderTests
{
    private static Song S(string artist, string title) => new(artist, title);

    private static IReadOnlyList<Song> Songs(string artist, int count, string prefix) => Enumerable.Range(1, count).Select(i => S(artist, $"{prefix}{i}")).ToList();

    [Fact]
    public void Songs_both_like_come_first()
    {
        var ana = new Taste("Ana", [S("ABBA", "Dancing Queen"), .. Songs("Ana's band", 10, "a")], []);
        var bo = new Taste("Bo", [.. Songs("Bo's band", 10, "b"), S("abba", "dancing queen")], []);

        var mix = Blender.Mix([ana, bo], 10);

        Assert.Equal("dancing queen", mix[0].Title.ToLowerInvariant());
        Assert.Equal(10, mix.Count);
        Assert.Equal(mix.Count, mix.Select(s => s.Key).Distinct().Count());
    }

    [Fact]
    public void Everyone_gets_turns()
    {
        var tastes = new[] { new Taste("A", Songs("A", 20, "a"), []), new Taste("B", Songs("B", 20, "b"), []), new Taste("C", Songs("C", 20, "c"), []) };

        var mix = Blender.Mix(tastes, 9);

        Assert.All(new[] { "A", "B", "C" }, artist => Assert.Equal(3, mix.Count(s => s.Artist == artist)));
    }

    [Fact]
    public void Loved_songs_and_shared_artists_move_up()
    {
        var ana = new Taste("Ana", [.. Songs("Solo", 5, "s"), S("Toto", "Rosanna")], [S("Solo", "s5")]);
        var bo = new Taste("Bo", [S("Toto", "Africa")], []);

        var mix = Blender.Mix([ana, bo], 10).Select(s => s.Title).ToList();

        // Her loved s5 before her s2; Rosanna (Bo likes Toto too) before her s4, though ranked last.
        Assert.True(mix.IndexOf("s5") < mix.IndexOf("s2"));
        Assert.True(mix.IndexOf("Rosanna") < mix.IndexOf("s4"));
    }

    [Fact]
    public void Runs_out_gracefully()
        => Assert.Equal(3, Blender.Mix([new Taste("A", Songs("A", 3, "a"), [])], 25).Count);
}
