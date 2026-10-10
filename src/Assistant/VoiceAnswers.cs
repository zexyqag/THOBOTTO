using System.Text.RegularExpressions;

namespace THOBOTTO.Assistant;

// Reads a spoken answer to a question with choices: which one (by number, or by what tells them apart),
// yes to the only one, or no.
public static partial class VoiceAnswers
{
    public const int Cancel = -1;

    private static readonly string[] Yes = ["yes", "yeah", "yep", "sure", "ok", "okay", "do it", "go ahead", "confirm", "please"];
    private static readonly string[] No = ["no", "nope", "cancel", "never mind", "nevermind", "dont", "stop", "forget it"];
    private static readonly string[] Ordinals = ["first", "second", "third"];
    private static readonly string[] Numbers = ["one", "two", "three"];
    // Words that don't tell choices apart.
    private static readonly HashSet<string> Filler = ["the", "one", "a", "an", "in", "that", "who", "is", "i", "mean", "with", "us", "me"];

    // The choice's index, Cancel, or null when it isn't an answer.
    public static int? Pick(string said, IReadOnlyList<string> labels)
    {
        var text = Spaces().Replace(NonWord().Replace(said.ToLowerInvariant().Replace("'", ""), " "), " ").Trim();
        if (No.Any(n => text == n || text.StartsWith(n + " ")))
            return Cancel;
        if (Yes.Any(y => text == y || text.StartsWith(y + " ")) && labels.Count == 1)
            return 0;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // By the words only one choice has ("the one in the call", "Ana Smith"), else by number ("the second").
        var telling = words.Where(w => !Filler.Contains(w)).ToList();
        var matches = labels.Select((l, i) => (i, Count: telling.Count(w => Words(l).Contains(w)))).Where(m => m.Count > 0).ToList();
        var best = matches.Count == 0 ? 0 : matches.Max(m => m.Count);
        if (matches.Count(m => m.Count == best) == 1)
            return matches.First(m => m.Count == best).i;
        foreach (var word in words)
        {
            var at = Array.IndexOf(Ordinals, word) is >= 0 and var o ? o : Array.IndexOf(Numbers, word) is >= 0 and var n ? n : int.TryParse(word, out var d) ? d - 1 : word == "last" ? labels.Count - 1 : -1;
            if (at >= 0 && at < labels.Count)
                return at;
        }
        return null;
    }

    private static HashSet<string> Words(string label) => NonWord().Replace(label.ToLowerInvariant(), " ").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
