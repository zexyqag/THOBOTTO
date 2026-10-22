using THOBOTTO.Helpers;

namespace THOBOTTO.Tests;

public class PersonalityEffectsFileTests
{
    private const string Base = """{ "name": "Unit 404", "lines": { "joined": ["[spell]404[/spell] [pause] online in {channel}."] }""";

    [Fact]
    public void Effects_and_marks_in_a_file_are_read()
    {
        var (file, problem) = PersonalityFile.Parse(Base + """, "effects": { "glitch": 0.3, "bitcrush": 8 } }""");
        Assert.Null(problem);
        Assert.Equal(0.3, file!.Effects!["glitch"]);
    }

    [Fact]
    public void A_bad_effect_or_an_unclosed_mark_is_refused()
    {
        Assert.Equal("\"glitch\" goes from 0 to 1.", PersonalityFile.Parse(Base + """, "effects": { "glitch": 3 } }""").Problem);
        Assert.Equal("A line for \"joined\": [slow] isn't closed with [/slow].", PersonalityFile.Parse("""{ "name": "X", "lines": { "joined": ["[slow]hi"] } }""").Problem);
    }

    [Fact]
    public void The_templates_carry_what_they_declare()
    {
        var unit = PersonalityFile.Templates.Single(t => t.Name == "Unit 404");
        Assert.Equal(8, unit.Effects!["bitcrush"]);
        Assert.Equal(12, PersonalityFile.Templates.Single(t => t.Name == "Zorp").Effects!["pitch"]);
    }

    [Fact]
    public void A_kokoro_blend_is_a_voice_name()
        => Assert.Null(PersonalityFile.Parse("""{ "name": "X", "lines": {}, "voices": { "kokoro": "af_sky(2)+af_bella(1)" } }""").Problem);
}
