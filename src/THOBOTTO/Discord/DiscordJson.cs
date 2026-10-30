using System.Text.Json;

namespace THOBOTTO.Discord;

// Discord's ids outgrow the numbers JavaScript reads exactly, so JSON meant for other programs carries them as strings.
public static class DiscordJson
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
}
