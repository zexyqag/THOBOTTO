using System.Collections.Concurrent;

namespace THOBOTTO.Listening;

// One step in handling a sentence: when, what, how long it took, and what came of it.
public sealed record TraceStep(DateTimeOffset At, string Step, int? Ms, string? Detail);

// A sentence said in voice, from the moment its speaker stopped, step by step.
public sealed class TraceSentence(long id, ulong guildId, ulong channelId, ulong userId, DateTimeOffset ended)
{
    private readonly List<TraceStep> _steps = [];

    public long Id => id;

    public ulong GuildId => guildId;

    public ulong ChannelId => channelId;

    public ulong UserId => userId;

    public DateTimeOffset Ended => ended;

    public IReadOnlyList<TraceStep> Steps
    {
        get
        {
            lock (_steps)
                return _steps.ToList();
        }
    }

    public void Add(TraceStep step)
    {
        lock (_steps)
            _steps.Add(step);
    }
}

// Voice debugging (a voice commands setting): while a server has it on, what happens to each sentence said there
// is kept for the panel's voice log, to see where a reply was slow or what went wrong. In memory only: a restart,
// or turning it off, forgets it.
public sealed class VoiceTrace(TimeProvider time)
{
    private const int Kept = 200;

    private readonly ConcurrentDictionary<ulong, LinkedList<TraceSentence>> _guilds = new();
    private readonly ConcurrentDictionary<ulong, byte> _on = new();
    private long _next;

    public bool On(ulong guildId) => _on.ContainsKey(guildId);

    // From the server's setting; off forgets what was kept.
    public void Follow(ulong guildId, bool on)
    {
        if (on)
            _on[guildId] = 0;
        else if (_on.TryRemove(guildId, out _))
            _guilds.TryRemove(guildId, out _);
    }

    // A sentence to follow, while debugging is on there.
    public TraceSentence? Begin(ulong guildId, ulong channelId, ulong userId, double seconds)
    {
        if (!On(guildId))
            return null;
        var sentence = new TraceSentence(Interlocked.Increment(ref _next), guildId, channelId, userId, time.GetUtcNow());
        sentence.Add(new(sentence.Ended, "stopped talking", null, $"{seconds:0.0} s of speech"));
        var list = _guilds.GetOrAdd(guildId, _ => new());
        lock (list)
        {
            list.AddFirst(sentence);
            while (list.Count > Kept)
                list.RemoveLast();
        }
        return sentence;
    }

    public void Step(TraceSentence? sentence, string step, int? ms = null, string? detail = null)
        => sentence?.Add(new(time.GetUtcNow(), step, ms, detail));

    public IReadOnlyList<TraceSentence> Recent(ulong guildId)
    {
        if (!_guilds.TryGetValue(guildId, out var list))
            return [];
        lock (list)
            return list.ToList();
    }
}
