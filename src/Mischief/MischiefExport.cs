using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Archive;
using THOBOTTO.Data;
using THOBOTTO.Discord;

namespace THOBOTTO.Mischief;

// Every rename of the server (the name history) and every paid effect (shields, locks, paint), for the archive's export.
public sealed class MischiefExport(IDbContextFactory<BotDbContext> dbFactory) : IExportSource
{
    public string Name => "mischief";

    public async Task WriteAsync(ulong guildId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        json.WriteStartObject();
        json.WriteStartArray("renames");
        var written = 0;
        await foreach (var rename in db.Renames.AsNoTracking().Where(r => r.GuildId == guildId).OrderBy(r => r.Id).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteNumber("id", rename.Id);
            json.WriteId("actor", rename.ActorId);
            json.WriteId("target", rename.TargetId);
            json.WriteString("oldName", rename.OldName);
            json.WriteString("newName", rename.NewName);
            json.WriteString("reason", rename.Reason);
            json.WriteNumber("cost", rename.Cost);
            json.WriteString("at", rename.CreatedAt);
            json.WriteEndObject();
            await json.FlushEveryAsync(++written);
        }
        json.WriteEndArray();
        json.WriteStartArray("effects");
        await foreach (var effect in db.MischiefEffects.AsNoTracking().Where(e => e.GuildId == guildId).OrderBy(e => e.Id).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteNumber("id", effect.Id);
            json.WriteString("kind", effect.Kind);
            json.WriteId("target", effect.TargetId);
            json.WriteId("actor", effect.ActorId);
            json.WriteNumber("paid", effect.Paid);
            json.WriteString("at", effect.CreatedAt);
            json.WriteString("endsAt", effect.EndsAt);
            json.WriteId("endedBy", effect.EndedById);
            json.WriteId("role", effect.RoleId);
            json.WriteEndObject();
            await json.FlushEveryAsync(++written);
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }
}
