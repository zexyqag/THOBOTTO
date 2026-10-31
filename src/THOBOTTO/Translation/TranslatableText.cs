using System.Text;
using System.Text.RegularExpressions;

namespace THOBOTTO.Translation;

// A message cut into what's said, which is translated, and what's kept as it is: code, links, emojis, mentions of
// roles and channels, spoilers' marks, line starts, and the line breaks between. Translated whole, the model drops
// emojis and spoilers and spaces links apart. Bold, italics and the like are dropped: a word translated alone loses
// its sense ("**meget** øl" became "**very** beer"). A member mentioned is said by name, so the sentence around them
// stays whole, and mentioned again where the name comes back.
public static partial class TranslatableText
{
    public sealed record Part(string Text, bool Said);

    public sealed record Pieces(IReadOnlyList<Part> Parts, IReadOnlyDictionary<ulong, string> Names)
    {
        public IReadOnlyList<string> Said => [.. Parts.Where(p => p.Said).Select(p => p.Text.Trim())];
    }

    public static Pieces Split(string text, Func<ulong, string?> name)
    {
        var names = new Dictionary<ulong, string>();
        text = UserMention().Replace(text, m =>
        {
            var id = ulong.Parse(m.Groups[1].Value);
            if (name(id) is not { Length: > 0 } named)
                return m.Value;
            names[id] = named;
            return $"@{named}";
        });
        var parts = new List<Part>();
        var at = 0;
        foreach (Match kept in Kept().Matches(text))
        {
            if (kept.Index > at)
                parts.Add(Words(text[at..kept.Index]));
            parts.Add(new(kept.Value, false));
            at = kept.Index + kept.Length;
        }
        if (at < text.Length)
            parts.Add(Words(text[at..]));
        return new(parts, names);
    }

    // Spaces and punctuation alone aren't sent.
    private static Part Words(string text) => text.Any(char.IsLetter) ? new(Marks().Replace(text, ""), true) : new(text, false);

    // In pieces under 20 characters, at spaces.
    public static IReadOnlyList<string> Short(IEnumerable<string> said)
    {
        var groups = new List<string>();
        var group = "";
        foreach (var word in said.SelectMany(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (group.Length > 0 && group.Length + 1 + word.Length >= 20)
            {
                groups.Add(group);
                group = "";
            }
            group = group.Length == 0 ? word : $"{group} {word}";
        }
        if (group.Length > 0)
            groups.Add(group);
        return groups;
    }

    // The translations, in the order Said gave them, put back between what was kept; spaces around each as they were.
    public static string Join(Pieces pieces, IReadOnlyList<string> translated)
    {
        var text = new StringBuilder();
        var next = 0;
        foreach (var part in pieces.Parts)
        {
            if (!part.Said)
            {
                text.Append(part.Text);
                continue;
            }
            var lead = part.Text.Length - part.Text.TrimStart().Length;
            var trail = part.Text.Length - part.Text.TrimEnd().Length;
            text.Append(part.Text, 0, lead).Append(translated[next++].Trim()).Append(part.Text, part.Text.Length - trail, trail);
        }
        // The model writes "20:00" as "20: 00" and "@Ana" as "@ Ana".
        var joined = Time().Replace(text.ToString(), "$1:$2");
        foreach (var (id, named) in pieces.Names)
            joined = Regex.Replace(joined, $"@ ?{Regex.Escape(named)}", $"<@{id}>");
        return joined;
    }

    [GeneratedRegex(@"<@!?(\d+)>")]
    private static partial Regex UserMention();

    [GeneratedRegex(@"```[\s\S]*?```|`[^`\n]+`|<a?:\w+:\d+>|<t:-?\d+(?::[tTdDfFR])?>|</[^>:\n]+:\d+>|<@[!&]?\d+>|<#\d+>|<id:\w+>"
        + @"|<?https?://[^\s<>()]+>?|\[(?=[^\]\n]*\]\()|\]\(|^(?:\#{1,3}|-\#|>>>|>|[-*]|\d+\.)[ ]|\|\||\n|[\p{So}\p{Cs}\u200d\ufe0f\u20e3]+",
        RegexOptions.Multiline)]
    private static partial Regex Kept();

    // Bold, italics, underline, strikethrough; a lone "*" between spaces is kept.
    [GeneratedRegex(@"\*\*|__|~~|\*(?=\S)|(?<=\S)\*")]
    private static partial Regex Marks();

    [GeneratedRegex(@"(\d): (\d\d)\b")]
    private static partial Regex Time();
}
