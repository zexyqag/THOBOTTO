using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Members;

namespace THOBOTTO.Live;

// The streams a member was seen live with.
public sealed class LiveMemberData(IDbContextFactory<BotDbContext> dbFactory) : IMemberData
{
    public string Name => "Streams";

    public async Task<long> CountAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.LiveStreams.LongCountAsync(s => s.GuildId == guildId && s.UserId == userId);
    }

    public async Task WriteAsync(ulong guildId, ulong userId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        json.WriteStartArray();
        await foreach (var stream in db.LiveStreams.AsNoTracking().Where(s => s.GuildId == guildId && s.UserId == userId).OrderBy(s => s.StartedAt).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteString("platform", stream.Platform);
            json.WriteString("url", stream.Url);
            json.WriteString("title", stream.Title);
            json.WriteString("game", stream.Game);
            json.WriteString("startedAt", stream.StartedAt);
            if (stream.EndedAt is { } ended)
                json.WriteString("endedAt", ended);
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }
}
