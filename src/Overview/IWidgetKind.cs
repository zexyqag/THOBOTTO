using NetCord.Rest;

namespace THOBOTTO.Overview;

// An option a widget kind takes, with its default as text.
public sealed record WidgetOption(string Key, string Label, string Default, string? Help = null);

// What a widget shows: a line of text, an embed, or both.
public sealed record WidgetView(string? Content, EmbedProperties? Embed);

// A kind of widget a feature offers for the overview channels: what it's called, what it takes, how often it's
// looked at again, and what it shows for a server.
public interface IWidgetKind
{
    string Id { get; }

    string Name { get; }

    string Description { get; }

    IReadOnlyList<WidgetOption> Options => [];

    TimeSpan Refresh { get; }

    // Null when there's nothing to show yet; the board then shows the kind's name and "nothing yet".
    Task<WidgetView?> RenderAsync(ulong guildId, IReadOnlyDictionary<string, string> options);
}

public static class WidgetOptions
{
    // The options as stored, with the kind's defaults for whatever's missing.
    public static IReadOnlyDictionary<string, string> Of(IWidgetKind kind, string json)
    {
        var stored = new Dictionary<string, string>();
        try
        {
            stored = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return kind.Options.ToDictionary(o => o.Key, o => stored.GetValueOrDefault(o.Key) is { Length: > 0 } v ? v : o.Default);
    }

    public static int Int(IReadOnlyDictionary<string, string> options, string key, int fallback, int min, int max)
        => int.TryParse(options.GetValueOrDefault(key), out var value) ? Math.Clamp(value, min, max) : fallback;
}
