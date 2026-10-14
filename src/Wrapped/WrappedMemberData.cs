using System.Globalization;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Members;

namespace THOBOTTO.Wrapped;

// A member's time in voice channels, and how long they talked each day.
public sealed class VoiceTimeMemberData(IDbContextFactory<BotDbContext> dbFactory) : IMemberData
{
    public string Name => "Voice time";

    public async Task<long> CountAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.VoiceSessions.LongCountAsync(s => s.GuildId == guildId && s.UserId == userId);
    }

    public async Task WriteAsync(ulong guildId, ulong userId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        json.WriteStartObject();
        json.WriteStartArray("sessions");
        await foreach (var session in db.VoiceSessions.AsNoTracking().Where(s => s.GuildId == guildId && s.UserId == userId).OrderBy(s => s.Id).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteId("channel", session.ChannelId);
            json.WriteString("joined", session.JoinedAt);
            if (session.LeftAt is { } left)
                json.WriteString("left", left);
            else
                json.WriteNull("left");
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("talk");
        await foreach (var day in db.TalkTimes.AsNoTracking().Where(t => t.GuildId == guildId && t.UserId == userId).OrderBy(t => t.Day).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteString("day", day.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            json.WriteNumber("ms", day.Ms);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }
}

// Whether the member asked to be named in the server's Wrapped, and since when.
public sealed class WrappedNameMemberData(IDbContextFactory<BotDbContext> dbFactory) : IMemberData
{
    public string Name => "Named in Wrapped";

    public async Task<long> CountAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.WrappedOptIns.LongCountAsync(o => o.GuildId == guildId && o.UserId == userId);
    }

    public async Task WriteAsync(ulong guildId, ulong userId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.WrappedOptIns.AsNoTracking().FirstOrDefaultAsync(o => o.GuildId == guildId && o.UserId == userId) is { } optIn)
        {
            json.WriteStartObject();
            json.WriteString("since", optIn.At);
            json.WriteEndObject();
        }
        else
            json.WriteNullValue();
    }
}
