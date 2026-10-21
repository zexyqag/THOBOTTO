using System.Buffers.Binary;
using System.Text;

using NetCord.Gateway.Voice;

namespace THOBOTTO.Sounds;

// Ogg Opus (what Discord serves its soundboard's uploaded sounds as): the Ogg pages read into packets, the
// first ("OpusHead") giving the channels and the samples to skip, the rest decoded with Opus at 48 kHz.
public static class OggOpus
{
    public const int Rate = 48_000;
    // Opus frames are at most 120 ms: 5760 samples per channel at 48 kHz.
    private const int MostFrameSamples = 5760;

    public static bool Is(ReadOnlySpan<byte> bytes) => bytes.StartsWith("OggS"u8) && bytes.IndexOf("OpusHead"u8) >= 0;

    // Interleaved samples (left, right, … for stereo) at 48 kHz, and the channels. Null when it isn't Ogg Opus.
    public static (short[] Samples, int Channels)? Decode(byte[] bytes)
    {
        var packets = Packets(bytes);
        if (packets.Count < 2 || !packets[0].AsSpan().StartsWith("OpusHead"u8) || packets[0].Length < 12)
            return null;
        var head = packets[0];
        int channels = head[9];
        if (channels is not (1 or 2))
            return null;
        var skip = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10, 2));
        using var decoder = new OpusDecoder(channels == 2 ? VoiceChannels.Stereo : VoiceChannels.Mono);
        var frame = new short[MostFrameSamples * channels];
        var samples = new List<short>();
        // The second packet is the tags; audio follows.
        foreach (var packet in packets.Skip(2))
        {
            var count = decoder.Decode(packet, frame, MostFrameSamples, false);
            samples.AddRange(frame.AsSpan(0, count * channels));
        }
        var start = Math.Min(skip * channels, samples.Count);
        return (samples.ToArray()[start..], channels);
    }

    // The packets of the first logical stream: a page's segment table says how its segments join into packets,
    // a 255-byte segment meaning the packet goes on.
    private static List<byte[]> Packets(byte[] bytes)
    {
        var packets = new List<byte[]>();
        var packet = new List<byte>();
        uint? serial = null;
        var at = 0;
        while (at + 27 <= bytes.Length && bytes.AsSpan(at, 4).SequenceEqual("OggS"u8))
        {
            var pageSerial = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 14, 4));
            int segments = bytes[at + 26];
            var table = at + 27;
            var data = table + segments;
            if (data > bytes.Length)
                break;
            serial ??= pageSerial;
            var mine = pageSerial == serial;
            for (var i = 0; i < segments; i++)
            {
                int length = bytes[table + i];
                if (data + length > bytes.Length)
                    return packets;
                if (mine)
                {
                    packet.AddRange(bytes.AsSpan(data, length));
                    if (length < 255)
                    {
                        packets.Add([.. packet]);
                        packet.Clear();
                    }
                }
                data += length;
            }
            at = data;
        }
        return packets;
    }

}
