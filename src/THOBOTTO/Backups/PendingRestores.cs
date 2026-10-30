using System.Collections.Concurrent;

namespace THOBOTTO.Backups;

// An uploaded backup between its preview and the go-ahead: kept a while for the one who uploaded it.
public sealed class PendingRestores(TimeProvider time)
{
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<Guid, (ulong Guild, ulong User, string Json, DateTimeOffset At)> _pending = new();

    public Guid Put(ulong guildId, ulong userId, string json)
    {
        foreach (var old in _pending.Where(p => time.GetUtcNow() - p.Value.At > Keep).Select(p => p.Key).ToList())
            _pending.TryRemove(old, out _);
        var id = Guid.NewGuid();
        _pending[id] = (guildId, userId, json, time.GetUtcNow());
        return id;
    }

    public string? Find(Guid id, ulong guildId, ulong userId)
        => _pending.TryGetValue(id, out var p) && p.Guild == guildId && p.User == userId && time.GetUtcNow() - p.At <= Keep ? p.Json : null;

    public void Forget(Guid id) => _pending.TryRemove(id, out _);
}
