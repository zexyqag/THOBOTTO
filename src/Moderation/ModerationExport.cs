using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Archive;
using THOBOTTO.Data;

namespace THOBOTTO.Moderation;

// Every case of the server and every appeal, with their reasons, details and text: the server's own history, for
// the archive's export.
public sealed class ModerationExport(IDbContextFactory<BotDbContext> dbFactory) : IExportSource
{
    public string Name => "moderation";

    public async Task WriteAsync(ulong guildId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        json.WriteStartObject();
        json.WriteStartArray("cases");
        var written = 0;
        await foreach (var c in db.ModCases.AsNoTracking().Where(c => c.GuildId == guildId).OrderBy(c => c.Number).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteNumber("number", c.Number);
            json.WriteString("type", c.Type);
            json.WriteId("target", c.TargetId);
            json.WriteId("channel", c.ChannelId);
            json.WriteId("role", c.RoleId);
            json.WriteId("moderator", c.ModeratorId);
            json.WriteString("reason", c.Reason);
            json.WriteString("details", c.Details);
            json.WriteString("createdAt", c.CreatedAt);
            json.WriteTime("endsAt", c.EndsAt);
            json.WriteTime("endedAt", c.EndedAt);
            json.WriteTime("pardonedAt", c.PardonedAt);
            json.WriteId("pardonedBy", c.PardonedById);
            json.WriteString("pardonReason", c.PardonReason);
            json.WriteId("logChannel", c.LogChannelId);
            json.WriteId("logMessage", c.LogMessageId);
            json.WriteEndObject();
            await json.FlushEveryAsync(++written);
        }
        json.WriteEndArray();
        json.WriteStartArray("appeals");
        await foreach (var appeal in db.ModAppeals.AsNoTracking().Where(a => a.GuildId == guildId).OrderBy(a => a.Id).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteNumber("id", appeal.Id);
            json.WriteId("member", appeal.UserId);
            json.WriteNumber("caseNumber", appeal.CaseNumber);
            json.WriteString("text", appeal.Text);
            json.WriteString("createdAt", appeal.CreatedAt);
            json.WriteId("channel", appeal.ChannelId);
            json.WriteId("message", appeal.MessageId);
            json.WriteString("decision", appeal.Decision);
            json.WriteId("decidedBy", appeal.DecidedById);
            json.WriteTime("decidedAt", appeal.DecidedAt);
            json.WriteEndObject();
            await json.FlushEveryAsync(++written);
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }
}
