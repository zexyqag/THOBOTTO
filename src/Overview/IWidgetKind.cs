namespace THOBOTTO.Overview;

// An option a widget kind takes, with its default as text; with choices, only those; long for a text of lines.
public sealed record WidgetOption(string Key, string Label, string Default, string? Help = null, IReadOnlyList<WidgetChoice>? Choices = null, bool Long = false);

public sealed record WidgetChoice(string Value, string Label);

// A piece of what a cell shows: a heading (may be empty), a markdown text, and buttons under it.
public sealed record WidgetBlock(string Title, string Text, IReadOnlyList<WidgetButton>? Buttons = null);

// A button its module answers by the custom id.
public sealed record WidgetButton(string CustomId, string Label, string? Emoji = null);

// A kind of block a feature offers for the overview channels' widgets: what it's called, what it takes, how often
// it's looked at again, and what it shows for a server.
public interface IWidgetKind
{
    string Id { get; }

    string Name { get; }

    string Description { get; }

    IReadOnlyList<WidgetOption> Options => [];

    TimeSpan Refresh { get; }

    // Empty when there's nothing to show yet; several for a kind that shows things one by one (a card each).
    Task<IReadOnlyList<WidgetBlock>> RenderAsync(ulong guildId, IReadOnlyDictionary<string, string> options);
}

public static class WidgetOptions
{
    // How many of a kind's pieces sit side by side when its cell has the row to itself (1 to 3).
    public const string Columns = "columns";

    // The options as stored, with the kind's defaults for whatever's missing or not among its choices.
    public static IReadOnlyDictionary<string, string> Of(IWidgetKind kind, IReadOnlyDictionary<string, string> stored)
        => kind.Options.ToDictionary(o => o.Key, o => stored.GetValueOrDefault(o.Key) is { Length: > 0 } v && (o.Choices is null || o.Choices.Any(c => c.Value == v)) ? v : o.Default);

    public static int Int(IReadOnlyDictionary<string, string> options, string key, int fallback, int min, int max)
        => int.TryParse(options.GetValueOrDefault(key), out var value) ? Math.Clamp(value, min, max) : fallback;
}
