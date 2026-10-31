using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Members;

namespace THOBOTTO.GameServers;

// The game server whitelists a member is on through the bot, and as what.
public sealed class WhitelistMemberData(IDbContextFactory<BotDbContext> dbFactory) : IMemberData
{
    public string Name => "Game server whitelists";

    public async Task<long> CountAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.WhitelistEntries.LongCountAsync(w => w.GuildId == guildId && w.UserId == userId);
    }

    public async Task WriteAsync(ulong guildId, ulong userId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        json.WriteStartArray();
        await foreach (var entry in db.WhitelistEntries.AsNoTracking().Where(w => w.GuildId == guildId && w.UserId == userId)
            .Join(db.GameServers, w => w.ServerId, s => s.Id, (w, s) => new { w.Name, w.AddedAt, Server = s.Name ?? s.Host }).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteString("server", entry.Server);
            json.WriteString("as", entry.Name);
            json.WriteString("addedAt", entry.AddedAt);
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }
}
