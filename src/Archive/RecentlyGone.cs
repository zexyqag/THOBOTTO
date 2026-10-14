namespace THOBOTTO.Archive;

// Ids the gateway said are gone while the archive held no row to mark: a fetch or a listing asked before the
// deletion may still bring them, and must not take them for live. Only the Archiver's writer touches this.
public sealed class RecentlyGone(TimeSpan keep)
{
    private readonly Dictionary<ulong, DateTimeOffset> _at = [];

    public void Note(ulong id, DateTimeOffset at)
    {
        foreach (var old in _at.Where(kv => kv.Value < at - keep).Select(kv => kv.Key).ToList())
            _at.Remove(old);
        _at[id] = at;
    }

    public bool TryGet(ulong id, out DateTimeOffset at) => _at.TryGetValue(id, out at);
}
