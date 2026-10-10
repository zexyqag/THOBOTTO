namespace THOBOTTO.Speaking;

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
