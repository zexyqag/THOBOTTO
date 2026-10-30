using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Archive;
using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Members;

namespace THOBOTTO.Mischief;

// The renames a member did and the ones done to them.
public sealed class RenamesMemberData(IDbContextFactory<BotDbContext> dbFactory) : IMemberData
{
    public string Name => "Renames";

    public async Task<long> CountAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Renames.LongCountAsync(r => r.GuildId == guildId && (r.ActorId == userId || r.TargetId == userId));
    }

    public async Task WriteAsync(ulong guildId, ulong userId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        json.WriteStartArray();
        await foreach (var rename in db.Renames.AsNoTracking().Where(r => r.GuildId == guildId && (r.ActorId == userId || r.TargetId == userId)).OrderBy(r => r.Id).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteString("at", rename.CreatedAt);
            json.WriteId("by", rename.ActorId);
            json.WriteId("of", rename.TargetId);
            json.WriteString("from", rename.OldName);
            json.WriteString("to", rename.NewName);
            json.WriteString("reason", rename.Reason);
            json.WriteNumber("cost", rename.Cost);
            json.WriteTime("done", rename.DoneAt);
            json.WriteTime("declined", rename.DeclinedAt);
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }
}
