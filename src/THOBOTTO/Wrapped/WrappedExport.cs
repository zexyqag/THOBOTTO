using System.Globalization;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Archive;
using THOBOTTO.Data;
using THOBOTTO.Discord;

namespace THOBOTTO.Wrapped;

// Every member's time in voice and how long they talked each day, who asked to be named, and the Wrappeds the bot
// posted on its own, for the archive's export.
public sealed class WrappedExport(IDbContextFactory<BotDbContext> dbFactory) : IExportSource
{
    public string Name => "wrapped";

    public async Task WriteAsync(ulong guildId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        json.WriteStartObject();
        json.WriteStartArray("voiceSessions");
        var written = 0;
        await foreach (var session in db.VoiceSessions.AsNoTracking().Where(s => s.GuildId == guildId).OrderBy(s => s.Id).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteNumber("id", session.Id);
            json.WriteId("member", session.UserId);
            json.WriteId("channel", session.ChannelId);
            json.WriteString("joinedAt", session.JoinedAt);
            json.WriteTime("leftAt", session.LeftAt);
            json.WriteString("seenAt", session.SeenAt);
            json.WriteEndObject();
            await json.FlushEveryAsync(++written);
        }
        json.WriteEndArray();
        json.WriteStartArray("talkTime");
        await foreach (var day in db.TalkTimes.AsNoTracking().Where(t => t.GuildId == guildId).OrderBy(t => t.Day).ThenBy(t => t.UserId).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteId("member", day.UserId);
            json.WriteString("day", day.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            json.WriteNumber("ms", day.Ms);
            json.WriteEndObject();
            await json.FlushEveryAsync(++written);
        }
        json.WriteEndArray();
        json.WriteStartArray("optIns");
        await foreach (var optIn in db.WrappedOptIns.AsNoTracking().Where(o => o.GuildId == guildId).OrderBy(o => o.At).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteId("member", optIn.UserId);
            json.WriteString("at", optIn.At);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("posts");
        await foreach (var post in db.WrappedPosts.AsNoTracking().Where(p => p.GuildId == guildId).OrderBy(p => p.At).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteString("key", post.Key);
            json.WriteString("at", post.At);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }
}
