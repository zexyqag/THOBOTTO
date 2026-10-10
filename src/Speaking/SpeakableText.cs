using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace THOBOTTO.Speaking;

public static partial class SpeakableText
{
    // Mentions should already be names; any left are dropped, as are links, custom emoji, markdown and pictographs.
    public static string From(string text, int most)
    {
        var plain = Link().Replace(text, m => m.Groups["label"].Success ? m.Groups["label"].Value : "");
        plain = Mention().Replace(plain, "");
        plain = CustomEmoji().Replace(plain, "");
        plain = Markup().Replace(plain, "");
        // Emoji and other symbols (▶️, 🎙️), and what joins them.
        plain = string.Concat(plain.EnumerateRunes().Where(r => Rune.GetUnicodeCategory(r) is not (UnicodeCategory.OtherSymbol or UnicodeCategory.Format or UnicodeCategory.NonSpacingMark) && r.Value != 0xFE0F).Select(r => r.ToString()));
        plain = Spaces().Replace(plain, " ").Trim(' ', '·', '-', ':');
        if (plain.Length <= most)
            return plain;
        var cut = plain[..most];
        var end = cut.LastIndexOfAny(['.', '!', '?']);
        return end > most / 3 ? cut[..(end + 1)] : cut.TrimEnd() + "…";
    }

    [GeneratedRegex(@"\[(?<label>[^\]]*)\]\([^)]*\)|https?://\S+")]
    private static partial Regex Link();

    [GeneratedRegex(@"<(?:@[!&]?|#)\d+>|<t:\d+(?::[a-zA-Z])?>")]
    private static partial Regex Mention();

    [GeneratedRegex(@"<a?:\w+:\d+>")]
    private static partial Regex CustomEmoji();

    [GeneratedRegex(@"[*_`~>|]+|^-# ", RegexOptions.Multiline)]
    private static partial Regex Markup();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
