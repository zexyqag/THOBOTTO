using System.Collections.Concurrent;

namespace THOBOTTO.Listening;

public sealed record Said(ulong UserId, string Text, DateTimeOffset At, long Id);

// The last minutes said in each voice channel by members the helpers may hear, in memory only, so a voice quote
// can be taken from it and the Activity can show it as captions. Commands to the helpers aren't kept.
public sealed class VoiceTranscript(TimeProvider time)
{
    private static readonly TimeSpan Kept = TimeSpan.FromMinutes(10);
    private const int MostLines = 200;

    private readonly ConcurrentDictionary<ulong, List<Said>> _channels = new();
    private long _lastId;

    public void Add(Heard heard)
    {
        var lines = _channels.GetOrAdd(heard.ChannelId, _ => []);
        lock (lines)
        {
            lines.Add(new(heard.UserId, heard.Text, time.GetUtcNow(), Interlocked.Increment(ref _lastId)));
            Trim(lines);
        }
    }

    public IReadOnlyList<Said> Recent(ulong channelId)
    {
        if (!_channels.TryGetValue(channelId, out var lines))
            return [];
        lock (lines)
        {
            Trim(lines);
            return lines.ToList();
        }
    }

    private void Trim(List<Said> lines)
    {
        lines.RemoveAll(l => time.GetUtcNow() - l.At > Kept);
        if (lines.Count > MostLines)
            lines.RemoveRange(0, lines.Count - MostLines);
    }
}
