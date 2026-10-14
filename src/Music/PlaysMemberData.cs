using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Members;

namespace THOBOTTO.Music;

// The songs a member asked the helpers for.
public sealed class PlaysMemberData(IDbContextFactory<BotDbContext> dbFactory) : IMemberData
{
    public string Name => "Songs played";

    public async Task<long> CountAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.PlayRecords.LongCountAsync(p => p.GuildId == guildId && p.RequestedBy == userId);
    }

    public async Task WriteAsync(ulong guildId, ulong userId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        json.WriteStartArray();
        await foreach (var play in db.PlayRecords.AsNoTracking().Where(p => p.GuildId == guildId && p.RequestedBy == userId).OrderBy(p => p.Id).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteString("at", play.StartedAt);
            json.WriteString("artist", play.Artist);
            json.WriteString("title", play.Title);
            json.WriteString("source", play.Source);
            json.WriteString("uri", play.Uri);
            json.WriteNumber("lengthMs", play.LengthMs);
            json.WriteNumber("playedMs", play.PlayedMs);
            json.WriteBoolean("skipped", play.Skipped);
            json.WriteId("channel", play.VoiceChannelId);
            json.WriteId("helper", play.HelperId);
            json.WriteString("personality", play.Personality);
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }
}
