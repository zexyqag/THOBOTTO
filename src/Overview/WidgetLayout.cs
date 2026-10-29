using System.Text.Json;

namespace THOBOTTO.Overview;

public sealed record WidgetCell(string Kind, Dictionary<string, string> Options);

// A widget's rows of cells, as stored.
public static class WidgetLayout
{
    // Within Discord's 40 components a message (a box per cell) and 25 fields an embed.
    public const int MostRows = 5;
    public const int MostCells = 3;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static List<List<WidgetCell>> Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<List<WidgetCell>>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Write(List<List<WidgetCell>> rows) => JsonSerializer.Serialize(rows, Json);

    public static bool AddRow(List<List<WidgetCell>> rows, string kind)
    {
        if (rows.Count >= MostRows)
            return false;
        rows.Add([new(kind, [])]);
        return true;
    }

    public static bool AddCell(List<List<WidgetCell>> rows, int row, string kind)
    {
        if (row < 0 || row >= rows.Count || rows[row].Count >= MostCells)
            return false;
        rows[row].Add(new(kind, []));
        return true;
    }

    public static bool Remove(List<List<WidgetCell>> rows, int row, int cell)
    {
        if (!Has(rows, row, cell))
            return false;
        rows[row].RemoveAt(cell);
        rows.RemoveAll(r => r.Count == 0);
        return true;
    }

    // A cell moves along its row, or to the end of the row above or below (a new row past the last); rows left
    // empty go.
    public static bool Move(List<List<WidgetCell>> rows, int row, int cell, int across, int down)
    {
        if (!Has(rows, row, cell))
            return false;
        if (down == 0)
        {
            var to = cell + across;
            if (to < 0 || to >= rows[row].Count)
                return false;
            (rows[row][cell], rows[row][to]) = (rows[row][to], rows[row][cell]);
            return true;
        }
        var target = row + down;
        if (target < 0 || (target == rows.Count && (rows[row].Count == 1 || rows.Count >= MostRows)) || (target < rows.Count && rows[target].Count >= MostCells))
            return false;
        if (target == rows.Count)
            rows.Add([]);
        rows[target].Add(rows[row][cell]);
        rows[row].RemoveAt(cell);
        rows.RemoveAll(r => r.Count == 0);
        return true;
    }

    public static bool MoveRow(List<List<WidgetCell>> rows, int row, int by)
    {
        var to = row + by;
        if (row < 0 || row >= rows.Count || to < 0 || to >= rows.Count)
            return false;
        (rows[row], rows[to]) = (rows[to], rows[row]);
        return true;
    }

    private static bool Has(List<List<WidgetCell>> rows, int row, int cell) => row >= 0 && row < rows.Count && cell >= 0 && cell < rows[row].Count;
}

public static class WidgetStyles
{
    public const string CardsLines = "cards-lines";
    public const string CardsBoxes = "cards-boxes";
    public const string CardsPlain = "cards-plain";
    public const string ColumnsBoth = "columns-both";
    public const string ColumnsBars = "columns-bars";
    public const string ColumnsRows = "columns-rows";
    public const string ColumnsPlain = "columns-plain";

    public static readonly IReadOnlyList<(string Id, string Name)> All =
    [
        (CardsLines, "Cards: one box, lines between"),
        (CardsBoxes, "Cards: a box for each"),
        (CardsPlain, "Cards: one box, no lines"),
        (ColumnsBoth, "Columns: lines between rows and columns"),
        (ColumnsBars, "Columns: lines between columns"),
        (ColumnsRows, "Columns: lines between rows"),
        (ColumnsPlain, "Columns: no lines"),
    ];

    public static bool Cards(string style) => style.StartsWith("cards", StringComparison.Ordinal);
}
