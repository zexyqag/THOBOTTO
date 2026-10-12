using THOBOTTO.Listening;
using THOBOTTO.Voice.Hearing;

namespace THOBOTTO.Tests;

public class AddressedTests
{
    private static readonly string[] Names = ["Jeeves", "Javes", "DJ Volume"];

    [Theory]
    [InlineData("Jeeves, play ABBA.", "Jeeves", "play ABBA")]
    [InlineData("jeeves skip", "Jeeves", "skip")]
    [InlineData("Javes, queue Lazy Generation", "Javes", "queue Lazy Generation")]
    [InlineData("DJ Volume, turn it down!", "DJ Volume", "turn it down")]
    public void A_sentence_starting_with_a_name_is_for_that_helper(string heard, string name, string said)
        => Assert.Equal((name, said), VoiceCommands.AddressedTo(heard, Names));

    [Theory]
    [InlineData("Let's play Fortnite later.")]
    [InlineData("I asked Jeeves to play it.")]
    [InlineData("Jeeves.")]
    public void Anything_else_isnt(string heard)
        => Assert.Null(VoiceCommands.AddressedTo(heard, Names));
}

public class WhisperWindowTests
{
    [Theory]
    [InlineData(16_000 * 2, 256)]
    [InlineData(16_000 * 8, 500)]
    [InlineData(16_000 * 40, 1500)]
    public void The_window_fits_the_sentence(int samples, int steps)
        => Assert.Equal(steps, LocalWhisper.AudioContext(samples));
}

public class NoiseTests
{
    private static short[] Tone(double seconds, double loudness)
        => Enumerable.Range(0, (int)(seconds * 48_000)).Select(i => (short)(loudness * Math.Sin(i * 0.1))).ToArray();

    [Fact]
    public void Speech_goes_on() => Assert.True(VoiceEars.Speechlike(Tone(1.0, 3000)));

    [Fact]
    public void Quiet_doesnt() => Assert.False(VoiceEars.Speechlike(Tone(1.0, 100)));

    [Fact]
    public void Clicks_dont()
    {
        // 2 s, loud for one frame in ten.
        var samples = Tone(2.0, 100);
        for (var f = 0; f < 100; f += 10)
            for (var i = f * 960; i < (f + 1) * 960; i++)
                samples[i] = (short)(8000 * Math.Sin(i * 0.3));
        Assert.False(VoiceEars.Speechlike(samples));
    }

    [Theory]
    [InlineData("[BLANK_AUDIO]", "")]
    [InlineData("(keyboard clicking)", "")]
    [InlineData("Jeeves, skip [BLANK_AUDIO]", "Jeeves, skip")]
    public void Whispers_noise_notes_are_nothing(string heard, string left)
        => Assert.Equal(left, VoiceEars.WithoutNoises(heard));
}

public class ResamplingTests
{
    private static short[] Tone(double hz) => [.. Enumerable.Range(0, 48_000).Select(i => (short)(Math.Sin(2 * Math.PI * hz * i / 48_000) * 8000))];

    private static double Level(float[] samples) => Math.Sqrt(samples.Skip(200).Take(15_000).Average(s => (double)s * s));

    // Speech goes through; the hiss above 8 kHz doesn't fold down into it.
    [Fact]
    public void What_is_above_speech_is_filtered_out()
    {
        var speech = Level(VoiceEars.To16kHz(Tone(1000)));
        var hiss = Level(VoiceEars.To16kHz(Tone(12_000)));
        Assert.True(hiss < speech / 50, $"speech {speech:0.000}, hiss {hiss:0.000}");
    }
}
