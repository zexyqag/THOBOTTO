using System.Text.RegularExpressions;

using THOBOTTO.Listening;

namespace THOBOTTO.Quotes;

// Whose words a voice quote takes: someone else's last (the usual "quote that"), the asker's, a member's,
// or the last few lines whoever said them.
public abstract record QuoteWho
{
    public sealed record Others : QuoteWho;

    public sealed record Me : QuoteWho;

    public sealed record Named(string Name) : QuoteWho;

    public sealed record Person(ulong Id) : QuoteWho;

    public sealed record Last(int Lines) : QuoteWho;
}

public static partial class QuotePicker
{
    public const int MostLines = 10;

    private static readonly string[] Numbers = ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"];

    // From what followed "quote" (nothing, "that", "me", "the last three lines", a name).
    public static QuoteWho Read(string? said)
    {
        var text = NonWord().Replace((said ?? "").ToLowerInvariant().Replace("'", ""), " ").Trim();
        if (text is "" or "that" or "this" or "it" or "them" or "him" or "her")
            return new QuoteWho.Others();
        if (text is "me" or "myself" or "what i said" or "what i just said")
            return new QuoteWho.Me();
        if (LastLines().Match(text) is { Success: true } last)
        {
            var n = last.Groups["n"].Value;
            var count = int.TryParse(n, out var d) ? d : Array.IndexOf(Numbers, n) + 1;
            return new QuoteWho.Last(Math.Clamp(count, 1, MostLines));
        }
        return new QuoteWho.Named(text);
    }

    // Where in the recent lines the quote starts and ends (end exclusive), or null when there's nothing.
    public static (int Start, int End)? Pick(IReadOnlyList<Said> recent, ulong asker, QuoteWho who)
    {
        int LastBy(Func<Said, bool> by)
        {
            for (var i = recent.Count - 1; i >= 0; i--)
            {
                if (by(recent[i]))
                    return i;
            }
            return -1;
        }
        var at = who switch
        {
            QuoteWho.Me => LastBy(s => s.UserId == asker),
            QuoteWho.Person(var id) => LastBy(s => s.UserId == id),
            QuoteWho.Last(var lines) => recent.Count == 0 ? -1 : Math.Max(0, recent.Count - lines),
            _ => LastBy(s => s.UserId != asker),
        };
        if (at < 0)
            return null;
        return who is QuoteWho.Last ? (at, recent.Count) : (at, at + 1);
    }

    [GeneratedRegex(@"^(?:the )?last (?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten)(?: lines?| things?| sentences?)?$")]
    private static partial Regex LastLines();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonWord();
}
