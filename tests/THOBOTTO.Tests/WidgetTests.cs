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
    public void A_clipped_block_ends_at_a_whole_line()
    {
        var lines = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"**line {i}** of the list"));
        var value = WidgetMessage.Build(null, 0, WidgetStyles.ColumnsPlain, [[new("A", lines)]]).Embed!.Fields!.Single().Value!;
        Assert.True(value.Length <= 1024);
        Assert.EndsWith(" of the list\n…", value);
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

    private static IReadOnlyList<WidgetBlock> Pieces(int n) => [.. Enumerable.Range(1, n).Select(i => new WidgetBlock($"P{i}", $"piece {i}"))];

    [Fact]
    public void A_cell_alone_in_its_row_spreads_its_pieces_over_its_columns()
    {
        var rows = WidgetLayout.Arrange([[new RenderedCell("Servers", Pieces(5), 2)], [new RenderedCell("Leaderboard", Pieces(1), 1)]]);
        Assert.Equal(["P1,P2", "P3,P4", "P5", "P1"], rows.Select(r => string.Join(",", r.Select(b => b.Title))));
    }

    [Fact]
    public void A_cell_sharing_its_row_puts_its_pieces_one_under_another()
    {
        var rows = WidgetLayout.Arrange([[new RenderedCell("Servers", Pieces(2), 2), new RenderedCell("Events", [], 1)]]);
        var row = Assert.Single(rows);
        Assert.Equal(new WidgetBlock("Servers", "**P1**\npiece 1\n\n**P2**\npiece 2"), row[0]);
        Assert.Equal(new WidgetBlock("Events", "Nothing yet."), row[1]);
    }

    [Fact]
    public void What_doesnt_fit_Discords_counts_is_left_out_and_counted()
    {
        IReadOnlyList<IReadOnlyList<WidgetBlock>> many = [.. Pieces(30).Chunk(2)];
        var fields = WidgetMessage.Build(null, 0, WidgetStyles.ColumnsRows, many).Embed!.Fields!.ToList();
        Assert.True(fields.Count <= 25);
        Assert.EndsWith("more don't fit.", fields[^1].Value);
        var box = WidgetMessage.Build("T", 0, WidgetStyles.CardsLines, many).Components.OfType<ComponentContainerProperties>().Single();
        Assert.Equal(40, 1 + box.Components.Count());
        var boxes = WidgetMessage.Build("T", 0, WidgetStyles.CardsBoxes, many).Components.ToList();
        Assert.True(boxes.Count + boxes.OfType<ComponentContainerProperties>().Count() <= 40);
    }

    [Fact]
    public void An_option_with_choices_takes_only_those()
    {
        var kind = new ChoiceKind();
        Assert.Equal("cards", WidgetOptions.Of(kind, new Dictionary<string, string> { ["show"] = "cards" })["show"]);
        Assert.Equal("lines", WidgetOptions.Of(kind, new Dictionary<string, string> { ["show"] = "nonsense" })["show"]);
    }

    private sealed class ChoiceKind : IWidgetKind
    {
        public string Id => "choice";

        public string Name => "Choice";

        public string Description => "";

        public IReadOnlyList<WidgetOption> Options => [new("show", "Show", "lines", Choices: [new("lines", "Lines"), new("cards", "Cards")])];

        public TimeSpan Refresh => TimeSpan.FromMinutes(1);

        public Task<IReadOnlyList<WidgetBlock>> RenderAsync(ulong guildId, IReadOnlyDictionary<string, string> options) => Task.FromResult<IReadOnlyList<WidgetBlock>>([]);
    }

    [Fact]
    public async Task A_text_block_without_a_heading_shows_just_its_text()
    {
        var text = new TextWidget();
        Assert.Empty(await text.RenderAsync(1, WidgetOptions.Of(text, new Dictionary<string, string>())));
        var block = Assert.Single(await text.RenderAsync(1, WidgetOptions.Of(text, new Dictionary<string, string> { ["text"] = "Rules: be nice" })));
        IReadOnlyList<IReadOnlyList<WidgetBlock>> rows = [[block]];
        var box = WidgetMessage.Build(null, 0, WidgetStyles.CardsLines, rows).Components.OfType<ComponentContainerProperties>().Single();
        Assert.Equal("Rules: be nice", box.Components.OfType<TextDisplayProperties>().Single().Content);
        Assert.Equal("\u200b", WidgetMessage.Build(null, 0, WidgetStyles.ColumnsPlain, rows).Embed!.Fields!.Single().Name);
    }

    private static IReadOnlyList<WidgetButton> Buttons(int n) => [.. Enumerable.Range(1, n).Select(i => new WidgetButton($"gamerole:{i}", $"Game {i}", i == 1 ? "🎮" : null))];

    [Fact]
    public void A_blocks_buttons_go_under_its_text_in_cards_and_under_the_embed_in_columns()
    {
        IReadOnlyList<IReadOnlyList<WidgetBlock>> rows = [[new("Games", "pick", Buttons(7))], [new("C", "three")]];
        var box = WidgetMessage.Build(null, 0, WidgetStyles.CardsLines, rows).Components.OfType<ComponentContainerProperties>().Single().Components.ToList();
        Assert.IsType<TextDisplayProperties>(box[0]);
        var buttonRows = box.OfType<ActionRowProperties>().ToList();
        Assert.Equal([5, 2], buttonRows.Select(r => r.Count()));
        Assert.Equal(1, box.IndexOf(buttonRows[0]));
        Assert.Equal("gamerole:1", ((ButtonProperties)buttonRows[0].First()).CustomId);

        var columns = WidgetMessage.Build(null, 0, WidgetStyles.ColumnsPlain, rows);
        Assert.Equal([5, 2], columns.Components.OfType<ActionRowProperties>().Select(r => r.Count()));
    }

    [Fact]
    public void Buttons_count_toward_Discords_components_and_change_the_fingerprint()
    {
        IReadOnlyList<IReadOnlyList<WidgetBlock>> rows = [[new("Games", "pick", Buttons(25))], .. Pieces(6).Select(p => (IReadOnlyList<WidgetBlock>)[p])];
        var box = WidgetMessage.Build(null, 0, WidgetStyles.CardsLines, rows).Components.OfType<ComponentContainerProperties>().Single();
        var count = 1 + box.Components.Count() + box.Components.OfType<ActionRowProperties>().Sum(r => r.Count());
        Assert.True(count <= 40, $"{count} components");
        Assert.NotEqual(
            WidgetMessage.Build(null, 0, WidgetStyles.CardsLines, [[new("Games", "pick", Buttons(2))]]).Fingerprint,
            WidgetMessage.Build(null, 0, WidgetStyles.CardsLines, [[new("Games", "pick", Buttons(3))]]).Fingerprint);
    }
}
