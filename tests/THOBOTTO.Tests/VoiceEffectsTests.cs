using THOBOTTO.Helpers;

namespace THOBOTTO.Tests;

public class VoiceEffectsTests
{
    private static short[] Tone(int rate, double seconds, int hz = 220)
        => [.. Enumerable.Range(0, (int)(rate * seconds)).Select(i => (short)(8000 * Math.Sin(2 * Math.PI * hz * i / rate)))];

    [Fact]
    public void Settings_are_read_with_defaults_for_the_rest()
    {
        var effects = VoiceEffects.From(new Dictionary<string, double> { ["pitch"] = 12, ["static"] = 0.2 });
        Assert.Equal(new VoiceEffects(Pitch: 12, Static: 0.2), effects);
        Assert.Equal(VoiceEffects.None, VoiceEffects.From(null));
    }

    [Fact]
    public void Unknown_or_out_of_range_settings_are_named()
    {
        Assert.Null(VoiceEffects.Problem(new Dictionary<string, double> { ["pitch"] = -12, ["bitcrush"] = 8 }));
        Assert.StartsWith("\"reverb\" isn't an effect", VoiceEffects.Problem(new Dictionary<string, double> { ["reverb"] = 1 }));
        Assert.Equal("\"pitch\" goes from -12 to 12.", VoiceEffects.Problem(new Dictionary<string, double> { ["pitch"] = 13 }));
    }

    [Fact]
    public void An_octave_up_halves_the_length()
    {
        var pitched = VoiceEffects.Pitched(Tone(22050, 1), VoiceEffects.Factor(12));
        Assert.Equal(11025, pitched.Length);
    }

    [Fact]
    public void Crushing_quantizes()
    {
        var crushed = VoiceEffects.Crushed([1000, -1000, 5], 8);
        Assert.All(crushed, s => Assert.Equal(0, s % 256));
        Assert.Equal(0, crushed[2]);
    }

    [Fact]
    public void Shaping_is_the_same_for_the_same_seed_and_keeps_the_length_but_for_echo()
    {
        var tone = Tone(22050, 0.5);
        var effects = new VoiceEffects(Static: 0.3, Stutter: 0.5, Dropout: 0.3, Crackle: 0.4, Bitcrush: 8, Robot: 40);
        var once = effects.Apply(tone, 22050, 7);
        Assert.Equal(once, effects.Apply(tone, 22050, 7));
        Assert.Equal(tone.Length, once.Length);
        Assert.NotEqual(tone, once);
        Assert.True(new VoiceEffects(Echo: 0.5).Apply(tone, 22050, 7).Length > tone.Length);
    }

    [Fact]
    public void Nothing_set_changes_nothing()
        => Assert.Same(Tone(100, 0.1) is var tone ? tone : null, VoiceEffects.None.Apply(tone, 100, 1));
}
