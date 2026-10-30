using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Members;

namespace THOBOTTO.Points;

// A member's account and every entry of theirs in the ledger.
public sealed class PointsMemberData(IDbContextFactory<BotDbContext> dbFactory) : IMemberData
{
    public string Name => "Points";

    public async Task<long> CountAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.PointEntries.LongCountAsync(e => e.GuildId == guildId && e.UserId == userId);
    }

    public async Task WriteAsync(ulong guildId, ulong userId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        json.WriteStartObject();
        json.WritePropertyName("account");
        if (await db.PointAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.GuildId == guildId && a.UserId == userId) is { } account)
        {
            json.WriteStartObject();
            json.WriteNumber("balance", account.Balance);
            json.WriteNumber("voice", account.Voice);
            json.WriteNumber("chat", account.Chat);
            json.WriteNumber("received", account.Received);
            json.WriteNumber("given", account.Given);
            json.WriteString("updatedAt", account.UpdatedAt);
            json.WriteEndObject();
        }
        else
            json.WriteNullValue();
        json.WriteStartArray("entries");
        await foreach (var entry in db.PointEntries.AsNoTracking().Where(e => e.GuildId == guildId && e.UserId == userId).OrderBy(e => e.Id).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteString("at", entry.CreatedAt);
            json.WriteString("kind", entry.Kind);
            json.WriteBoolean("earned", entry.Earned);
            json.WriteNumber("amount", entry.Amount);
            json.WriteString("reason", entry.Reason);
            json.WriteId("actor", entry.ActorId);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }
}
