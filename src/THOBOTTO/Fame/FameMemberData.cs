using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Members;

namespace THOBOTTO.Fame;

// The member's messages in the hall of fame.
public sealed class FameMemberData(IDbContextFactory<BotDbContext> dbFactory) : IMemberData
{
    public string Name => "Hall of fame";

    public async Task<long> CountAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.FameEntries.LongCountAsync(f => f.GuildId == guildId && f.AuthorId == userId);
    }

    public async Task WriteAsync(ulong guildId, ulong userId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        json.WriteStartArray();
        await foreach (var entry in db.FameEntries.AsNoTracking().Where(f => f.GuildId == guildId && f.AuthorId == userId).OrderBy(f => f.InductedAt).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteId("message", entry.MessageId);
            json.WriteId("channel", entry.ChannelId);
            json.WriteNumber("reactors", entry.Reactors);
            json.WriteString("inductedAt", entry.InductedAt);
            json.WriteId("showcaseMessage", entry.ShowcaseMessageId);
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }
}
