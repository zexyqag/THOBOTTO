namespace THOBOTTO.Archive;

// What a complete listing changes against the rows: ids never seen, known ones back from gone, and known ones
// gone, among those the listing can vouch for.
public sealed record SnapshotDiff(IReadOnlyList<ulong> New, IReadOnlyList<ulong> Back, IReadOnlyList<ulong> Gone)
{
    // Known: each id with whether its row already says gone (left, deleted).
    public static SnapshotDiff Of(IReadOnlyDictionary<ulong, bool> known, IReadOnlyCollection<ulong> listed, Func<ulong, bool>? vouched = null)
    {
        var listedSet = listed as IReadOnlySet<ulong> ?? listed.ToHashSet();
        var added = listed.Where(id => !known.ContainsKey(id)).Distinct().ToList();
        var back = listed.Where(id => known.TryGetValue(id, out var gone) && gone).Distinct().ToList();
        var missing = known.Where(k => !k.Value && !listedSet.Contains(k.Key) && (vouched is null || vouched(k.Key))).Select(k => k.Key).ToList();
        return new(added, back, missing);
    }

    public override string ToString() => $"{New.Count} new, {Back.Count} back, {Gone.Count} gone";
}
