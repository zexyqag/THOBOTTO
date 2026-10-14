using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Members;

namespace THOBOTTO.Quotes;

// The quotes a member saved and those with a line of theirs.
public sealed class QuotesMemberData(IDbContextFactory<BotDbContext> dbFactory) : IMemberData
{
    public string Name => "Quotes";

    public async Task<long> CountAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await Mine(db, guildId, userId).LongCountAsync();
    }

    public async Task WriteAsync(ulong guildId, ulong userId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        json.WriteStartArray();
        await foreach (var quote in Mine(db, guildId, userId).Include(q => q.Lines).OrderBy(q => q.Id).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteNumber("id", quote.Id);
            json.WriteString("saidAt", quote.SaidAt);
            json.WriteString("savedAt", quote.CreatedAt);
            json.WriteId("savedBy", quote.AddedById);
            json.WriteString("context", quote.Context);
            json.WriteId("channel", quote.ChannelId);
            json.WriteId("message", quote.MessageId);
            json.WriteStartArray("lines");
            foreach (var line in quote.Lines.OrderBy(l => l.Position))
            {
                json.WriteStartObject();
                json.WriteId("speaker", line.SpeakerId);
                json.WriteString("speakerName", line.SpeakerName);
                json.WriteString("text", line.Text);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    private static IQueryable<Quote> Mine(BotDbContext db, ulong guildId, ulong userId)
        => db.Quotes.AsNoTracking().Where(q => q.GuildId == guildId && (q.AddedById == userId || q.Lines.Any(l => l.SpeakerId == userId)));
}
