namespace THOBOTTO.Helpers;

// Mono 16-bit speech at the engine's own rate.
public sealed record Spoken(short[] Samples, int Rate)
{
    public TimeSpan Length => TimeSpan.FromSeconds(Samples.Length / (double)Rate);
}

public static class Pcm
{
    public const int DiscordRate = 48_000;

    // To Discord's 48 kHz stereo (left, right, left, …), by linear interpolation.
    public static short[] ToDiscord(Spoken spoken)
    {
        var (samples, rate) = (spoken.Samples, spoken.Rate);
        if (samples.Length == 0)
            return [];
        var length = (int)((long)samples.Length * DiscordRate / rate);
        var stereo = new short[length * 2];
        for (var i = 0; i < length; i++)
        {
            var at = (double)i * rate / DiscordRate;
            var j = (int)at;
            var next = Math.Min(j + 1, samples.Length - 1);
            var value = (short)(samples[j] + (samples[next] - samples[j]) * (at - j));
            stereo[2 * i] = value;
            stereo[2 * i + 1] = value;
        }
        return stereo;
    }

    // Speech at an even loudness, whatever voice made it: brought to a set level (the percent of it), the peaks
    // rounded off rather than clipped.
    public static short[] Leveled(short[] samples, int percent)
    {
        if (samples.Length == 0)
            return samples;
        var rms = Math.Sqrt(samples.Average(s => (double)s * s)) / short.MaxValue;
        if (rms < 1e-4)
            return samples;
        var gain = Math.Clamp(SpeechLevel / rms, 0.5, 8) * percent / 100;
        var leveled = new short[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            var x = samples[i] / (double)short.MaxValue * gain;
            // Up to the knee as it is, then bent smoothly toward full scale.
            var y = Math.Abs(x) <= Knee ? x : Math.Sign(x) * (Knee + (1 - Knee) * Math.Tanh((Math.Abs(x) - Knee) / (1 - Knee)));
            leveled[i] = (short)Math.Round(y * short.MaxValue);
        }
        return leveled;
    }

    // About -16 dBFS: as loud as most mastered music.
    private const double SpeechLevel = 0.16;
    private const double Knee = 0.7;

    // A WAV file, for hearing a voice in the browser.
    public static byte[] Wav(Spoken spoken)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var data = spoken.Samples.Length * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + data);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(spoken.Rate);
        writer.Write(spoken.Rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(data);
        foreach (var sample in spoken.Samples)
            writer.Write(sample);
        writer.Flush();
        return stream.ToArray();
    }

    public static short[] FromBytes(ReadOnlySpan<byte> bytes)
    {
        var samples = new short[bytes.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)(bytes[2 * i] | bytes[2 * i + 1] << 8);
        return samples;
    }
}
