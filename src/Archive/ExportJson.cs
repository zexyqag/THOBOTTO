using System.Text.Json;

namespace THOBOTTO.Archive;

// What every file of the export writes the same way: ids as strings (Discord's don't fit the numbers JavaScript reads
// exactly), times as ISO-8601 or null, and stored JSON as the value it holds.
public static class ExportJson
{
    public static void WriteId(this Utf8JsonWriter json, string name, ulong id) => json.WriteString(name, id.ToString());

    public static void WriteId(this Utf8JsonWriter json, string name, ulong? id)
    {
        if (id is { } some)
            json.WriteString(name, some.ToString());
        else
            json.WriteNull(name);
    }

    public static void WriteIdValue(this Utf8JsonWriter json, ulong id) => json.WriteStringValue(id.ToString());

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
