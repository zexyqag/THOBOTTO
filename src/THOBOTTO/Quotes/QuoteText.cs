namespace THOBOTTO.Quotes;

public sealed record ParsedLine(string Speaker, string Text);

public static class QuoteText
{
    public const int MaxLines = 10;

    // "Name: what they said", one turn per line. A line without a name continues the previous turn.
    public static IReadOnlyList<ParsedLine>? Parse(string text)
    {
        var lines = new List<ParsedLine>();
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = raw.IndexOf(':');
            if (colon > 0 && colon <= 40 && !raw.AsSpan(colon + 1).StartsWith("//"))
                lines.Add(new(raw[..colon].Trim().TrimStart('@'), raw[(colon + 1)..].Trim()));
            else if (lines.Count > 0)
                lines[^1] = lines[^1] with { Text = $"{lines[^1].Text}\n{raw}" };
            else
                return null;
        }

        return lines.Count is > 0 and <= MaxLines && lines.All(l => l.Speaker.Length > 0 && l.Text.Length > 0) ? lines : null;
    }
}
