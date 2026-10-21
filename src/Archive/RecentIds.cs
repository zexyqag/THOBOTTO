namespace THOBOTTO.Archive;

// Ids with when something last happened to them, kept for a while: a deletion the archive had no row to mark, or
// the gateway's last write of a row, so an answer asked before isn't taken for newer. Only the Archiver's writer
// touches one.
public sealed class RecentIds(TimeSpan keep)
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
