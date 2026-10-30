using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Archive;
using THOBOTTO.Data;
using THOBOTTO.Discord;

namespace THOBOTTO.Fame;

// The hall of fame: every inducted message, and the reactions counted towards one, for the archive's export.
public sealed class FameExport(IDbContextFactory<BotDbContext> dbFactory) : IExportSource
{
    public string Name => "hall-of-fame";

    public async Task WriteAsync(ulong guildId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        json.WriteStartObject();
        json.WriteStartArray("entries");
        var written = 0;
        await foreach (var entry in db.FameEntries.AsNoTracking().Where(e => e.GuildId == guildId).OrderBy(e => e.InductedAt).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteId("message", entry.MessageId);
            json.WriteId("channel", entry.ChannelId);
            json.WriteId("author", entry.AuthorId);
            json.WriteNumber("reactors", entry.Reactors);
            json.WriteId("showcaseMessage", entry.ShowcaseMessageId);
            json.WriteString("inductedAt", entry.InductedAt);
            json.WriteEndObject();
            await json.FlushEveryAsync(++written);
        }
        json.WriteEndArray();
        json.WriteStartArray("reactions");
        await foreach (var reaction in db.FameReactions.AsNoTracking().Where(r => r.GuildId == guildId).OrderBy(r => r.CreatedAt).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteId("message", reaction.MessageId);
            json.WriteId("reactor", reaction.ReactorId);
            json.WriteString("emoji", reaction.Emoji);
            json.WriteString("at", reaction.CreatedAt);
            json.WriteEndObject();
            await json.FlushEveryAsync(++written);
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }
}
