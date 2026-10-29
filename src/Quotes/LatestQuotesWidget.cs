using THOBOTTO.Overview;

namespace THOBOTTO.Quotes;

// The newest quotes.
public sealed class LatestQuotesWidget(QuoteBook book) : IWidgetKind
{
    public string Id => "quotes";

    public string Name => "Latest quotes";

    public string Description => "the newest quotes saved here";

    public IReadOnlyList<WidgetOption> Options => [new("count", "How many", "3", "1 to 10")];

    public TimeSpan Refresh => TimeSpan.FromMinutes(5);

    public async Task<WidgetBlock?> RenderAsync(ulong guildId, IReadOnlyDictionary<string, string> options)
    {
        var quotes = await book.LatestAsync(guildId, WidgetOptions.Int(options, "count", 3, 1, 10));
        if (quotes.Count == 0)
            return null;
        var text = string.Join("\n\n", quotes.Select(q => string.Join('\n', q.Lines.OrderBy(l => l.Position).Select(l => $"**{QuoteCommands.Speaker(l)}**: {l.Text}")) + $"\n-# #{q.Id} · <t:{q.SaidAt.ToUnixTimeSeconds()}:D>"));
        return new("Latest quotes", text);
    }
}
