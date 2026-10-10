using THOBOTTO.Listening;

namespace THOBOTTO.Assistant;

// How well something said matches a title or an option (0 to 1): as a whole, or by its words
// ("friday raid" matches "Friday night raid").
public static class Likeness
{
    public static double Of(string said, string text)
    {
        var (saidWords, textWords) = (Words(said), Words(text));
        if (saidWords.Count == 0 || textWords.Count == 0)
            return 0;
        var whole = VoiceCommandParser.Similarity(string.Concat(saidWords), string.Concat(textWords));
        var found = saidWords.Count(w => textWords.Any(t => VoiceCommandParser.Similarity(w, t) >= 0.8)) / (double)saidWords.Count;
        return Math.Max(whole, found);
    }

    private static List<string> Words(string text)
        => text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => string.Concat(w.Where(char.IsLetterOrDigit))).Where(w => w.Length > 0 && w is not ("the" or "a" or "an" or "for" or "on" or "to" or "in")).ToList();
}
