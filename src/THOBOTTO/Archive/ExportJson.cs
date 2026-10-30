using System.Text.Json;

namespace THOBOTTO.Archive;

// What every file of the export writes the same way: times as ISO-8601 or null, stored JSON as the value it holds, and
// ids as DiscordJson writes them.
public static class ExportJson
{
    // A writer holds what it wrote until it's flushed; a long array is flushed every so many rows so it never holds
    // the whole of it.
    public static Task FlushEveryAsync(this Utf8JsonWriter json, int written, int every = 1000)
        => written % every == 0 ? json.FlushAsync() : Task.CompletedTask;

    public static void WriteTime(this Utf8JsonWriter json, string name, DateTimeOffset? at)
    {
        if (at is { } some)
            json.WriteString(name, some);
        else
            json.WriteNull(name);
    }

    public static void WriteNumber(this Utf8JsonWriter json, string name, int? value)
    {
        if (value is { } some)
            json.WriteNumber(name, some);
        else
            json.WriteNull(name);
    }

    public static void WriteNumber(this Utf8JsonWriter json, string name, long? value)
    {
        if (value is { } some)
            json.WriteNumber(name, some);
        else
            json.WriteNull(name);
    }

    // Stored JSON (a message's raw, a snapshot) as the value it holds; null when there is none.
    public static void WriteJson(this Utf8JsonWriter json, string name, string? stored)
    {
        json.WritePropertyName(name);
        json.WriteJsonValue(stored);
    }

    public static void WriteJsonValue(this Utf8JsonWriter json, string? stored)
    {
        if (stored is null)
        {
            json.WriteNullValue();
            return;
        }
        using var document = JsonDocument.Parse(stored);
        document.RootElement.WriteTo(json);
    }
}
