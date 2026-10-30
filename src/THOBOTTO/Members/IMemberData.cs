using System.Text.Json;

namespace THOBOTTO.Members;

// What a feature keeps of a member in a server, for the member to see and take with them: how many rows, for the
// You page, and the rows themselves, written as the value of a property named after the source in the download.
public interface IMemberData
{
    // As the member reads it: "Points", "Quotes", "Songs played".
    string Name { get; }

    Task<long> CountAsync(ulong guildId, ulong userId);

    Task WriteAsync(ulong guildId, ulong userId, Utf8JsonWriter json);
}
