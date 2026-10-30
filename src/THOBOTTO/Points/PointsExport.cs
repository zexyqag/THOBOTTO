using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Archive;
using THOBOTTO.Data;
using THOBOTTO.Discord;

namespace THOBOTTO.Points;

// Every member's account and the whole ledger of the server, for the archive's export.
public sealed class PointsExport(IDbContextFactory<BotDbContext> dbFactory) : IExportSource
{
    public string Name => "points";

    public async Task WriteAsync(ulong guildId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        json.WriteStartObject();
        json.WriteStartArray("accounts");
        var written = 0;
        await foreach (var account in db.PointAccounts.AsNoTracking().Where(a => a.GuildId == guildId).OrderBy(a => a.UserId).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteId("member", account.UserId);
            json.WriteNumber("balance", account.Balance);
            json.WriteNumber("voice", account.Voice);
            json.WriteNumber("chat", account.Chat);
            json.WriteNumber("received", account.Received);
            json.WriteNumber("given", account.Given);
            json.WriteString("updatedAt", account.UpdatedAt);
            json.WriteTime("lastChatAt", account.LastChatAt);
            json.WriteNumber("pendingEarned", account.PendingEarned);
            json.WriteNumber("pendingExpired", account.PendingExpired);
            json.WriteTime("pendingSince", account.PendingSince);
            json.WriteEndObject();
            await json.FlushEveryAsync(++written);
        }
        json.WriteEndArray();
        json.WriteStartArray("ledger");
        await foreach (var entry in db.PointEntries.AsNoTracking().Where(e => e.GuildId == guildId).OrderBy(e => e.Id).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteNumber("id", entry.Id);
            json.WriteId("member", entry.UserId);
            json.WriteNumber("amount", entry.Amount);
            json.WriteString("kind", entry.Kind);
            json.WriteString("reason", entry.Reason);
            json.WriteId("actor", entry.ActorId);
            json.WriteString("at", entry.CreatedAt);
            json.WriteEndObject();
            await json.FlushEveryAsync(++written);
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }
}
