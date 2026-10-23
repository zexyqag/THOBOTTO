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

    // Words over hiss, with silence between: the words come to the level, not the whole line's average.
    [Fact]
    public void Hiss_tails_and_silences_dont_count()
    {
        var random = new Random(1);
        var line = new short[96_000];
        for (var i = 0; i < line.Length; i++)
        {
            var words = i / 9_600 % 3 == 0;
            line[i] = (short)((words ? Math.Sin(i * 0.05) * 0.04 : 0) * short.MaxValue + random.Next(-200, 200));
        }
        var leveled = Pcm.Leveled(line, 100);
        var wordRms = Rms([.. leveled.Where((_, i) => i / 9_600 % 3 == 0)]);
        Assert.InRange(wordRms, 0.14, 0.18);
        Assert.InRange(Pcm.LoudPart(line), 0.025, 0.035);
    }

    [Fact]
    public void A_very_quiet_line_comes_up_further()
        => Assert.InRange(Rms(Pcm.Leveled(Tone(0.02), 100)), 0.14, 0.17);
}
