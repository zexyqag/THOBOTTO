using System.Text.Json;

using THOBOTTO.Discord;

namespace THOBOTTO.Members;

// The download of a member's data in a server: the server, the member, when it was taken, then each source's rows
// under its name ("Songs played" is songsPlayed), in the order the sources are given.
public static class MemberDataFile
{
    public static string FileName(ulong guildId, ulong userId, DateTimeOffset taken) => $"thobotto-{guildId}-{userId}-{taken:yyyy-MM-dd}.json";

    public static async Task WriteAsync(Stream output, ulong guildId, ulong userId, DateTimeOffset taken, IEnumerable<IMemberData> sources)
    {
        await using var json = new Utf8JsonWriter(output, new() { Indented = true });
        json.WriteStartObject();
        json.WriteId("server", guildId);
        json.WriteId("member", userId);
        json.WriteString("taken", taken);
        foreach (var source in sources)
        {
            json.WritePropertyName(PropertyName(source.Name));
            await source.WriteAsync(guildId, userId, json);
            await json.FlushAsync();
        }
        json.WriteEndObject();
    }

    public static string PropertyName(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Concat(words.Select((word, i) => i == 0 ? word.ToLowerInvariant() : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
    }

    // Discord's ids are bigger than the numbers JavaScript reads exactly, so they're text, as Discord sends them.

}
