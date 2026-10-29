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
    private const string Empty = "Nothing in this widget yet.";
    private static readonly string Rule = new('─', 30);

    public bool Cards => Embed is null;

    public static WidgetMessage Build(string? title, int colour, string style, IReadOnlyList<IReadOnlyList<WidgetBlock>> rows)
        => WidgetStyles.Cards(style) ? CardsOf(title, colour, style, rows) : ColumnsOf(title, colour, style, rows);

    private static WidgetMessage CardsOf(string? title, int colour, string style, IReadOnlyList<IReadOnlyList<WidgetBlock>> rows)
    {
        var budget = Budget(CardsText - (title?.Length ?? 0), rows);
        var texts = rows.Select(r => r.Select(b => Clip($"### {b.Title}\n{b.Text}", budget)).ToList()).ToList();
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
                var (name, value) = (Clip(rows[r][c].Title, 200), Clip(rows[r][c].Text, budget));
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

    private static string Clip(string text, int most) => text.Length <= most ? text : text[..(most - 1)] + "…";

    // The look's name first: a widget that changes between cards and columns is posted anew.
    private static string Hash(string look, string style, string? title, int colour, IEnumerable<string> parts)
        => look + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u0001", [style, title ?? "", colour.ToString(), .. parts]))));

    public static string LookOf(string? fingerprint) => fingerprint?.Split(':')[0] ?? "";
}
