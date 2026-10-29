using NetCord;
using NetCord.Rest;

using THOBOTTO.Overview;

namespace THOBOTTO.Tests;

public class WidgetTests
{
    private static readonly IReadOnlyList<IReadOnlyList<WidgetBlock>> TwoRows = [[new("A", "one"), new("B", "two")], [new("C", "three")]];

    private static List<List<WidgetCell>> Cells(params int[] perRow)
        => [.. perRow.Select((n, r) => Enumerable.Range(0, n).Select(c => new WidgetCell($"{r}{c}", [])).ToList())];

    private static string Shape(List<List<WidgetCell>> rows) => string.Join("|", rows.Select(r => string.Join(",", r.Select(c => c.Kind))));

    [Fact]
    public void Cards_put_every_cell_in_one_box_with_lines_between()
    {
        var message = WidgetMessage.Build("Board", 0x123456, WidgetStyles.CardsLines, TwoRows);
        Assert.True(message.Cards);
        var box = Assert.IsType<ComponentContainerProperties>(Assert.Single(message.Components));
        Assert.Equal(new Color(0x123456), box.AccentColor);
        var parts = box.Components.ToList();
        Assert.Equal(["## Board", "### A\none", "### B\ntwo", "### C\nthree"], parts.OfType<TextDisplayProperties>().Select(t => t.Content));
        var lines = parts.OfType<ComponentSeparatorProperties>().ToList();
        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.True(l.Divider));
        // The line before a new row is the larger one.
        Assert.Equal([ComponentSeparatorSpacingSize.Small, ComponentSeparatorSpacingSize.Small, ComponentSeparatorSpacingSize.Large], lines.Select(l => l.Spacing));
    }

    [Fact]
    public void Cards_can_be_a_box_for_each()
    {
        var message = WidgetMessage.Build(null, 0x123456, WidgetStyles.CardsBoxes, TwoRows);
        Assert.Equal(3, message.Components.OfType<ComponentContainerProperties>().Count());
        Assert.Single(message.Components.OfType<ComponentSeparatorProperties>());
    }

    [Fact]
    public void Columns_put_rows_side_by_side_with_lines()
    {
        var message = WidgetMessage.Build("Board", 0x123456, WidgetStyles.ColumnsBoth, TwoRows);
        Assert.False(message.Cards);
        var fields = message.Embed!.Fields!.ToList();
        Assert.Equal(["A", "│ B", "​", "C"], fields.Select(f => f.Name));
        Assert.Equal("│ two", fields[1].Value);
        Assert.StartsWith("──", fields[2].Value);
        Assert.Equal([true, true, false, false], fields.Select(f => f.Inline));
        Assert.Equal("​", WidgetMessage.Build(null, 0, WidgetStyles.ColumnsPlain, TwoRows).Embed!.Fields!.ElementAt(2).Value);
    }

    [Fact]
    public void The_bar_goes_after_a_lines_own_markdown()
    {
        IReadOnlyList<IReadOnlyList<WidgetBlock>> rows = [[new("A", "one"), new("B", "two\n-# small\n> quoted")]];
        Assert.Equal("│ two\n-# │ small\n> │ quoted", WidgetMessage.Build(null, 0, WidgetStyles.ColumnsBars, rows).Embed!.Fields!.ElementAt(1).Value);
    }

    [Fact]
    public void A_long_block_is_clipped_to_its_share()
    {
        IReadOnlyList<IReadOnlyList<WidgetBlock>> rows = [[new("A", new string('x', 5000))]];
        Assert.True(WidgetMessage.Build(null, 0, WidgetStyles.ColumnsPlain, rows).Embed!.Fields!.Single().Value!.Length <= 1024);
        var text = WidgetMessage.Build(null, 0, WidgetStyles.CardsLines, rows).Components.OfType<ComponentContainerProperties>().Single().Components.OfType<TextDisplayProperties>().Single().Content;
        Assert.True(text.Length <= 4000);
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void The_fingerprint_tells_the_look_and_any_change()
    {
        var cards = WidgetMessage.Build(null, 0, WidgetStyles.CardsLines, TwoRows);
        Assert.Equal("cards", WidgetMessage.LookOf(cards.Fingerprint));
        Assert.Equal("columns", WidgetMessage.LookOf(WidgetMessage.Build(null, 0, WidgetStyles.ColumnsBars, TwoRows).Fingerprint));
        Assert.Equal(cards.Fingerprint, WidgetMessage.Build(null, 0, WidgetStyles.CardsLines, TwoRows).Fingerprint);
        Assert.NotEqual(cards.Fingerprint, WidgetMessage.Build(null, 0, WidgetStyles.CardsPlain, TwoRows).Fingerprint);
        Assert.NotEqual(cards.Fingerprint, WidgetMessage.Build(null, 0, WidgetStyles.CardsLines, [[new("A", "changed")]]).Fingerprint);
        Assert.Equal("", WidgetMessage.LookOf(null));
    }

    [Fact]
    public void A_layout_survives_writing_and_reading()
    {
        List<List<WidgetCell>> rows = [[new("leaderboard", new() { ["count"] = "5" })]];
        var read = WidgetLayout.Parse(WidgetLayout.Write(rows));
        Assert.Equal("leaderboard", read[0][0].Kind);
        Assert.Equal("5", read[0][0].Options["count"]);
        Assert.Empty(WidgetLayout.Parse("not json"));
    }

    [Fact]
    public void Cells_move_along_rows_and_between_them()
    {
        var rows = Cells(2, 1);
        Assert.True(WidgetLayout.Move(rows, 0, 0, 1, 0));
        Assert.Equal("01,00|10", Shape(rows));
        Assert.False(WidgetLayout.Move(rows, 0, 1, 1, 0));
        Assert.True(WidgetLayout.Move(rows, 1, 0, 0, -1));
        Assert.Equal("01,00,10", Shape(rows));
        // A full row takes no more; the last row's cell starts a new row.
        Assert.True(WidgetLayout.Move(rows, 0, 2, 0, 1));
        Assert.Equal("01,00|10", Shape(rows));
        Assert.False(WidgetLayout.Move(rows, 1, 0, 0, 1));
        Assert.False(WidgetLayout.Move(rows, 0, 0, 0, -1));
    }

    [Fact]
    public void Rows_and_cells_keep_to_the_limits()
    {
        var rows = Cells(3, 1, 1, 1, 1);
        Assert.False(WidgetLayout.AddRow(rows, "x"));
        Assert.False(WidgetLayout.AddCell(rows, 0, "x"));
        Assert.False(WidgetLayout.Move(rows, 1, 0, 0, -1));
        Assert.True(WidgetLayout.AddCell(rows, 1, "x"));
        Assert.True(WidgetLayout.Remove(rows, 4, 0));
        Assert.Equal(4, rows.Count);
        Assert.True(WidgetLayout.MoveRow(rows, 0, 1));
        Assert.Equal("10,x|00,01,02|20|30", Shape(rows));
        Assert.False(WidgetLayout.MoveRow(rows, 3, 1));
    }
}
