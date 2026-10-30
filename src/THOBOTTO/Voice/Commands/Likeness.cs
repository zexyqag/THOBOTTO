namespace THOBOTTO.Voice.Commands;

// How well something said matches a title or an option (0 to 1): as a whole, or by its words
// ("friday raid" matches "Friday night raid").
public static class Likeness
{
    public static double Of(string said, string text)
    {
        var (saidWords, textWords) = (Words(said), Words(text));
        if (saidWords.Count == 0 || textWords.Count == 0)
            return 0;
        var whole = Similarity(string.Concat(saidWords), string.Concat(textWords));
        var found = saidWords.Count(w => textWords.Any(t => Similarity(w, t) >= 0.8)) / (double)saidWords.Count;
        return Math.Max(whole, found);
    }

    // 1 minus the edit distance over the longer length.
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
            return 0;
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        var current = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return 1 - (double)previous[b.Length] / Math.Max(a.Length, b.Length);
    }

    private static List<string> Words(string text)
        => text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => string.Concat(w.Where(char.IsLetterOrDigit))).Where(w => w.Length > 0 && w is not ("the" or "a" or "an" or "for" or "on" or "to" or "in")).ToList();
}
