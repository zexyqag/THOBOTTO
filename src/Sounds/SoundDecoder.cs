using THOBOTTO.Speaking;

namespace THOBOTTO.Sounds;

// MP3, OGG (Vorbis) and WAV to mono speech-style audio, evened out so no sound is much louder than another.
public static class SoundDecoder
{
    // Loudness aimed for (RMS, of full scale), and the most a peak may reach.
    private const double TargetRms = 0.12;
    private const double MostPeak = 0.9;

    public static bool Known(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() is ".mp3" or ".ogg" or ".wav";

    // Null when it isn't audio it can read.
    public static Spoken? Decode(byte[] bytes, string fileName)
    {
        try
        {
            var (samples, rate) = Path.GetExtension(fileName).ToLowerInvariant() switch
            {
                ".mp3" => Mp3(bytes),
                ".ogg" => Ogg(bytes),
                ".wav" => Wav(bytes),
                _ => (null, 0),
            };
            return samples is { Length: > 0 } ? new(Even(samples), rate) : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or InvalidOperationException or IndexOutOfRangeException or NotSupportedException)
        {
            return null;
        }
    }

    private static (float[]?, int) Mp3(byte[] bytes)
    {
        using var file = new NLayer.MpegFile(new MemoryStream(bytes));
        return (Read((buffer, count) => file.ReadSamples(buffer, 0, count), file.Channels), file.SampleRate);
    }

    private static (float[]?, int) Ogg(byte[] bytes)
    {
        using var reader = new NVorbis.VorbisReader(new MemoryStream(bytes), true);
        return (Read((buffer, count) => reader.ReadSamples(buffer, 0, count), reader.Channels), reader.SampleRate);
    }

    // All of it, mixed down to mono.
    private static float[] Read(Func<float[], int, int> read, int channels)
    {
        var mono = new List<float>();
        var buffer = new float[4096 * channels];
        int got;
        while ((got = read(buffer, buffer.Length)) > 0)
        {
            for (var i = 0; i + channels <= got; i += channels)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++)
                    sum += buffer[i + c];
                mono.Add(sum / channels);
            }
        }
        return [.. mono];
    }

    // 16-bit PCM or 32-bit float WAV.
    private static (float[]?, int) Wav(byte[] bytes)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes));
        if (new string(reader.ReadChars(4)) != "RIFF")
            return (null, 0);
        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
            return (null, 0);
        int format = 0, channels = 0, rate = 0, bits = 0;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadInt32();
            if (id == "fmt ")
            {
                format = reader.ReadInt16();
                channels = reader.ReadInt16();
                rate = reader.ReadInt32();
                reader.ReadBytes(6);
                bits = reader.ReadInt16();
                reader.ReadBytes(size - 16);
            }
            else if (id == "data" && channels > 0)
            {
                var data = reader.ReadBytes(size);
                var frame = channels * bits / 8;
                var mono = new float[data.Length / frame];
                for (var i = 0; i < mono.Length; i++)
                {
                    float sum = 0;
                    for (var c = 0; c < channels; c++)
                    {
                        var at = i * frame + c * bits / 8;
                        sum += (format, bits) switch
                        {
                            (1, 16) => BitConverter.ToInt16(data, at) / 32768f,
                            (3, 32) => BitConverter.ToSingle(data, at),
                            _ => throw new NotSupportedException("WAV must be 16-bit PCM or 32-bit float."),
                        };
                    }
                    mono[i] = sum / channels;
                }
                return (mono, rate);
            }
            else
                reader.ReadBytes(size + (size & 1));
        }
        return (null, 0);
    }

    // Brought to an even loudness, with peaks kept under the top.
    private static short[] Even(float[] samples)
    {
        double sum = 0, peak = 0;
        foreach (var s in samples)
        {
            sum += s * s;
            peak = Math.Max(peak, Math.Abs(s));
        }
        var rms = Math.Sqrt(sum / samples.Length);
        var gain = rms > 0 ? Math.Min(TargetRms / rms, peak > 0 ? MostPeak / peak : 1) : 1;
        return samples.Select(s => (short)Math.Clamp(s * gain * 32767, short.MinValue, short.MaxValue)).ToArray();
    }
}
