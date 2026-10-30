namespace THOBOTTO.Overview;

// A heading and a note of the server's own.
public sealed class TextWidget : IWidgetKind
{
    public string Id => "text";

    public string Name => "Text";

    public string Description => "a heading and a note of your own";

    public IReadOnlyList<WidgetOption> Options =>
    [
        new("heading", "Heading", ""),
        new("text", "Text", "", "Discord's markdown works", Long: true),
    ];

    public TimeSpan Refresh => TimeSpan.FromHours(1);

    public Task<IReadOnlyList<WidgetBlock>> RenderAsync(ulong guildId, IReadOnlyDictionary<string, string> options)
    {
        var (heading, text) = (options.GetValueOrDefault("heading", ""), options.GetValueOrDefault("text", ""));
        return Task.FromResult<IReadOnlyList<WidgetBlock>>(heading.Length == 0 && text.Length == 0 ? [] : [new(heading, text)]);
    }
}
