using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Archive;
using THOBOTTO.Data;
using THOBOTTO.Discord;

namespace THOBOTTO.Quotes;

// Every quote of the server with its lines, for the archive's export.
public sealed class QuotesExport(IDbContextFactory<BotDbContext> dbFactory) : IExportSource
{
    public string Name => "quotes";

    public async Task WriteAsync(ulong guildId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        json.WriteStartArray();
        var written = 0;
        await foreach (var quote in db.Quotes.AsNoTracking().Where(q => q.GuildId == guildId).Include(q => q.Lines).OrderBy(q => q.Id).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteNumber("id", quote.Id);
            json.WriteId("addedBy", quote.AddedById);
            json.WriteString("context", quote.Context);
            json.WriteId("channel", quote.ChannelId);
            json.WriteId("message", quote.MessageId);
            json.WriteString("saidAt", quote.SaidAt);
            json.WriteString("createdAt", quote.CreatedAt);
            json.WriteStartArray("lines");
            foreach (var line in quote.Lines.OrderBy(l => l.Position))
            {
                json.WriteStartObject();
                json.WriteNumber("position", line.Position);
                json.WriteId("speaker", line.SpeakerId);
                json.WriteString("speakerName", line.SpeakerName);
                json.WriteString("text", line.Text);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
            await json.FlushEveryAsync(++written);
        }
        json.WriteEndArray();
    }
}
