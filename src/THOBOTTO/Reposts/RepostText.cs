using System.Text;
using System.Text.RegularExpressions;

namespace THOBOTTO.Reposts;

public static partial class RepostText
{
    // The text with each part outside `code` and ```code blocks``` rewritten; the code as written.
    public static string OutsideCode(string text, Func<string, string> rewrite)
    {
        var rewritten = new StringBuilder();
        var at = 0;
        foreach (Match code in Code().Matches(text))
        {
            rewritten.Append(rewrite(text[at..code.Index])).Append(code.Value);
            at = code.Index + code.Length;
        }
        return rewritten.Append(rewrite(text[at..])).ToString();
    }

    [GeneratedRegex(@"```[\s\S]*?```|`[^`\n]+`")]
    private static partial Regex Code();
}
