using System.Collections.Concurrent;

namespace THOBOTTO.Listening;

public sealed record Said(ulong UserId, string Text, DateTimeOffset At);

// The last couple of minutes said in each voice channel by members the helpers may hear, in memory only, so a
// voice quote can be taken from it. Commands to the helpers aren't kept.
public sealed class VoiceTranscript(TimeProvider time)
{
    private static readonly TimeSpan Kept = TimeSpan.FromMinutes(2);
    private const int MostLines = 40;

    private readonly ConcurrentDictionary<ulong, List<Said>> _channels = new();

    public void Add(Heard heard)
    {
        var lines = _channels.GetOrAdd(heard.ChannelId, _ => []);
        lock (lines)
        {
            lines.Add(new(heard.UserId, heard.Text, time.GetUtcNow()));
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
