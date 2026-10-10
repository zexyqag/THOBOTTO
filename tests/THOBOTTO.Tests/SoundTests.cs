using THOBOTTO.Sounds;

namespace THOBOTTO.Tests;

public class SoundTests
{
    // A second of a 440 Hz tone at 44.1 kHz, 16-bit stereo.
    private static byte[] Wav(double loudness)
    {
        const int rate = 44_100;
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        var samples = Enumerable.Range(0, rate).Select(i => (short)(loudness * 32767 * Math.Sin(2 * Math.PI * 440 * i / rate))).ToArray();
        w.Write("RIFF"u8); w.Write(36 + samples.Length * 4); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16); w.Write("data"u8); w.Write(samples.Length * 4);
        foreach (var s in samples)
        {
            w.Write(s);
            w.Write(s);
        }
        return stream.ToArray();
    }

    [Fact]
    public void Wav_is_read_at_its_length()
    {
        var sound = SoundDecoder.Decode(Wav(0.5), "tone.wav")!;
        Assert.Equal(44_100, sound.Rate);
        Assert.Equal(1.0, sound.Length.TotalSeconds, 2);
    }

    [Fact]
    public void Quiet_and_loud_come_out_alike()
    {
        static double Rms(short[] s) => Math.Sqrt(s.Average(x => (double)x * x));
        var quiet = Rms(SoundDecoder.Decode(Wav(0.05), "q.wav")!.Samples);
        var loud = Rms(SoundDecoder.Decode(Wav(0.9), "l.wav")!.Samples);
        Assert.InRange(quiet / loud, 0.9, 1.1);
    }

    [Theory]
    [InlineData("noise.wav")]
    [InlineData("noise.mp3")]
    [InlineData("noise.flac")]
    public void Garbage_is_no_sound(string name) => Assert.Null(SoundDecoder.Decode([1, 2, 3, 4, 5, 6, 7, 8], name));
}
