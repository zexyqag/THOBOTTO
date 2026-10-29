using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using NetCord;
using NetCord.Rest;

namespace THOBOTTO.Overview;

// A widget's message: cards (components, one column everywhere) or columns (an embed; up to three cells abreast on
// desktop, stacked on phones).
public sealed partial record WidgetMessage(IReadOnlyList<IMessageComponentProperties> Components, EmbedProperties? Embed, string Fingerprint)
{
    private const int CardsText = 4000;
    private const int EmbedText = 6000;
    private const int FieldText = 1024;
    private const int MostComponents = 40;
    private const int MostFields = 25;
    private const string Empty = "Nothing in this widget yet.";
    private static readonly string Rule = new('─', 30);

    public bool Cards => Embed is null;

    public static WidgetMessage Build(string? title, int colour, string style, IReadOnlyList<IReadOnlyList<WidgetBlock>> rows)
    {
        rows = Fit(title is { Length: > 0 }, style, rows);
        return WidgetStyles.Cards(style) ? CardsOf(title, colour, style, rows) : ColumnsOf(title, colour, style, rows);
    }

    // Within Discord's count of components (cards) or fields (columns): the last cells go, and the last one kept says
    // how many.
    private static IReadOnlyList<IReadOnlyList<WidgetBlock>> Fit(bool titled, string style, IReadOnlyList<IReadOnlyList<WidgetBlock>> rows)
    {
        var kept = rows.Select(r => r.ToList()).Where(r => r.Count > 0).ToList();
        var dropped = 0;
        while (kept.Count > 0 && Count(titled, style, kept) > (WidgetStyles.Cards(style) ? MostComponents : MostFields))
        {
            kept[^1].RemoveAt(kept[^1].Count - 1);
            if (kept[^1].Count == 0)
                kept.RemoveAt(kept.Count - 1);
            dropped++;
        }
        if (dropped > 0 && kept.Count > 0)
            kept[^1][^1] = kept[^1][^1] with { Text = $"{kept[^1][^1].Text}\n-# {(dropped == 1 ? "1 more doesn't" : $"{dropped} more don't")} fit." };
        return kept;
    }

    private static int Count(bool titled, string style, List<List<WidgetBlock>> rows)
    {
        var (cells, breaks, heading) = (rows.Sum(r => r.Count), rows.Count - 1, titled ? 1 : 0);
        return style switch
        {
            // A box and a text a cell, a gap between rows.
            WidgetStyles.CardsBoxes => heading + 2 * cells + breaks,
            // One box: a text a cell, a line before each but the first.
            _ when WidgetStyles.Cards(style) => 2 * (heading + cells),
            // A field a cell, one between rows.
            _ => cells + breaks,
        };
    }

    private static WidgetMessage CardsOf(string? title, int colour, string style, IReadOnlyList<IReadOnlyList<WidgetBlock>> rows)
    {
        var budget = Budget(CardsText - (title?.Length ?? 0), rows);
        var texts = rows.Select(r => r.Select(b => Clip(b.Title.Length > 0 ? $"### {b.Title}\n{b.Text}" : b.Text, budget)).ToList()).ToList();
        var heading = title is { Length: > 0 } ? new TextDisplayProperties($"## {title}") : null;
        List<IMessageComponentProperties> message = [];
        if (style == WidgetStyles.CardsBoxes)
        {
            if (heading is not null)
                message.Add(heading);
            for (var r = 0; r < texts.Count; r++)
            {
                if (r > 0)
                    message.Add(new ComponentSeparatorProperties { Divider = false, Spacing = ComponentSeparatorSpacingSize.Large });
                message.AddRange(texts[r].Select(text => new ComponentContainerProperties([new TextDisplayProperties(text)]) { AccentColor = new Color(colour) }));
            }
            if (message.Count == 0)
                message.Add(new TextDisplayProperties(Empty));
        }
        else
        {
            List<IComponentContainerComponentProperties> box = heading is null ? [] : [heading];
            for (var r = 0; r < texts.Count; r++)
            {
                for (var c = 0; c < texts[r].Count; c++)
                {
                    if (box.Count > 0)
                        box.Add(new ComponentSeparatorProperties { Divider = style != WidgetStyles.CardsPlain, Spacing = r > 0 && c == 0 ? ComponentSeparatorSpacingSize.Large : ComponentSeparatorSpacingSize.Small });
                    box.Add(new TextDisplayProperties(texts[r][c]));
                }
            }
            if (box.Count == 0)
                box.Add(new TextDisplayProperties(Empty));
            message.Add(new ComponentContainerProperties(box) { AccentColor = new Color(colour) });
        }
        return new(message, null, Hash("cards", style, title, colour, texts.SelectMany(t => t)));
    }

    private static WidgetMessage ColumnsOf(string? title, int colour, string style, IReadOnlyList<IReadOnlyList<WidgetBlock>> rows)
    {
        var bars = style is WidgetStyles.ColumnsBoth or WidgetStyles.ColumnsBars;
        var lines = style is WidgetStyles.ColumnsBoth or WidgetStyles.ColumnsRows;
        var budget = Math.Min(FieldText - 60, Budget(EmbedText - (title?.Length ?? 0), rows));
        List<EmbedFieldProperties> fields = [];
        for (var r = 0; r < rows.Count; r++)
        {
            if (r > 0)
                fields.Add(new() { Name = "​", Value = lines ? Rule : "​" });
            for (var c = 0; c < rows[r].Count; c++)
            {
                var (name, value) = (rows[r][c].Title.Length > 0 ? Clip(rows[r][c].Title, 200) : "\u200b", Clip(rows[r][c].Text, budget));
                if (bars && c > 0)
                    (name, value) = ("│ " + name, string.Join('\n', value.Split('\n').Select(Barred)));
                fields.Add(new() { Name = name, Value = value.Length > 0 ? value : "​", Inline = rows[r].Count > 1 });
            }
        }
        var embed = new EmbedProperties { Title = title, Color = new(colour), Fields = fields, Description = fields.Count == 0 ? Empty : null };
        return new([], embed, Hash("columns", style, title, colour, fields.Select(f => f.Name + "\n" + f.Value)));
    }

    // After a line's own markdown (subtext, heading, quote), which only works at the line's start.
    private static string Barred(string line) => LineStart().Match(line) is { Length: > 0 } start ? start.Value + "│ " + line[start.Length..] : "│ " + line;

    [GeneratedRegex(@"^(?:-#|#{1,3}|>{1,3}) ")]
    private static partial Regex LineStart();

    // Each cell's share of the message's text.
    private static int Budget(int total, IReadOnlyList<IReadOnlyList<WidgetBlock>> rows)
        => Math.Max(100, total / Math.Max(1, rows.Sum(r => r.Count)) - 20);

    // At the last whole line that fits, so no markdown is left open; mid-line only when that would lose too much.
    private static string Clip(string text, int most)
    {
        if (text.Length <= most)
            return text;
        var cut = text.LastIndexOf('\n', most - 2);
        return cut > most / 2 ? text[..cut] + "\n…" : text[..(most - 1)] + "…";
    }

    // The look's name first: a widget that changes between cards and columns is posted anew.
    private static string Hash(string look, string style, string? title, int colour, IEnumerable<string> parts)
        => look + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u0001", [style, title ?? "", colour.ToString(), .. parts]))));

    public static string LookOf(string? fingerprint) => fingerprint?.Split(':')[0] ?? "";
}
