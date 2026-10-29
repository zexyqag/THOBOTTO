namespace THOBOTTO.Overview;

// An option a widget kind takes, with its default as text.
public sealed record WidgetOption(string Key, string Label, string Default, string? Help = null);

// What a cell of a widget shows: a heading and a markdown text.
public sealed record WidgetBlock(string Title, string Text);

// A kind of block a feature offers for the overview channels' widgets: what it's called, what it takes, how often
// it's looked at again, and what it shows for a server.
public interface IWidgetKind
{
    string Id { get; }

    string Name { get; }

    string Description { get; }

    IReadOnlyList<WidgetOption> Options => [];

    TimeSpan Refresh { get; }

    // Null when there's nothing to show yet; the widget then says so under the kind's name.
    Task<WidgetBlock?> RenderAsync(ulong guildId, IReadOnlyDictionary<string, string> options);
}

public static class WidgetOptions
{
    // The options as stored, with the kind's defaults for whatever's missing.
    public static IReadOnlyDictionary<string, string> Of(IWidgetKind kind, IReadOnlyDictionary<string, string> stored)
        => kind.Options.ToDictionary(o => o.Key, o => stored.GetValueOrDefault(o.Key) is { Length: > 0 } v ? v : o.Default);

    public static int Int(IReadOnlyDictionary<string, string> options, string key, int fallback, int min, int max)
        => int.TryParse(options.GetValueOrDefault(key), out var value) ? Math.Clamp(value, min, max) : fallback;
}
