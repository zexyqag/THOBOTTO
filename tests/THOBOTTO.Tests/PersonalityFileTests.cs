using THOBOTTO.Helpers;

namespace THOBOTTO.Tests;

public class PersonalityFileTests
{
    [Fact]
    public void Every_template_file_loads_with_a_colour()
    {
        Assert.Equal(10, PersonalityFile.Templates.Count);
        Assert.Contains(PersonalityFile.Templates, t => t.Name == "Zeke");
        Assert.All(PersonalityFile.Templates.Append(PersonalityFile.Plain), t => Assert.NotNull(t.ColorValue));
    }

    [Fact]
    public void A_personality_comes_back_the_same_from_its_file()
    {
        var personality = new Personality
        {
            Name = "Jeeves",
            Color = 0x5B6770,
            Avatar = [1, 2, 3],
            AvatarType = "image/png",
            Phrases = new() { [Moments.Playing] = ["Now playing {track} 🎩"], [Moments.Skipped] = ["As you wish."] },
        };

        var json = PersonalityFile.From(personality).ToJson();
        var (file, problem) = PersonalityFile.Parse(json);

        Assert.Equal(["name", "color", "avatar", "lines"], System.Text.Json.JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name));

        Assert.Null(problem);
        Assert.Equal("Jeeves", file!.Name);
        Assert.Equal(0x5B6770, file.ColorValue);
        Assert.Equal([1, 2, 3], file.AvatarData!.Value.Bytes);
        Assert.Equal("image/png", file.AvatarData.Value.Type);
        Assert.Equal(["Now playing {track} 🎩"], file.Lines[Moments.Playing]);
        Assert.Equal("jeeves.json", file.FileName);
    }

    [Theory]
    [InlineData("not json", "isn't a personality file")]
    [InlineData("""{"lines": {}}""", "needs a name")]
    [InlineData("""{"name": "X", "color": "blue", "lines": {}}""", "colour")]
    [InlineData("""{"name": "X", "lines": {"dancing": ["hi"]}}""", "no moment called \"dancing\"")]
    [InlineData("""{"name": "X", "avatar": "data:text/html;base64,PGI+", "lines": {}}""", "PNG, JPEG, GIF or WebP")]
    public void Files_that_dont_fit_are_refused(string json, string expected)
    {
        var (file, problem) = PersonalityFile.Parse(json);
        Assert.Null(file);
        Assert.Contains(expected, problem);
    }

    [Fact]
    public void Lines_longer_than_editing_allows_are_refused()
    {
        var line = new string('a', PersonalityFile.MaxLineLength + 1);
        var json = "{\"name\": \"X\", \"lines\": {\"playing\": [\"" + line + "\"]}}";
        Assert.Contains("longer than", PersonalityFile.Parse(json).Problem);
    }
}
