using THOBOTTO.Helpers;

namespace THOBOTTO.Tests;

public class LevelingTests
{
    private static short[] Tone(double amplitude) => [.. Enumerable.Range(0, 48_000).Select(i => (short)(Math.Sin(i * 0.05) * amplitude * short.MaxValue))];

    private static double Rms(short[] samples) => Math.Sqrt(samples.Average(s => (double)s * s)) / short.MaxValue;

    [Fact]
    public void Quiet_speech_comes_up_to_the_level()
        => Assert.InRange(Rms(Pcm.Leveled(Tone(0.05), 100)), 0.14, 0.17);

    [Fact]
    public void Half_loudness_is_half_as_loud()
        => Assert.InRange(Rms(Pcm.Leveled(Tone(0.05), 50)) / Rms(Pcm.Leveled(Tone(0.05), 100)), 0.48, 0.52);

    // Turned up past full scale, the peaks are rounded off, never wrapped around.
    [Fact]
    public void Loud_speech_never_clips_over()
    {
        var leveled = Pcm.Leveled(Tone(0.9), 200);
        Assert.True(leveled.Max() <= short.MaxValue && leveled.Min() >= -short.MaxValue);
        Assert.True(leveled.Max() > short.MaxValue * 0.8);
    }
}
