using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Members;

namespace THOBOTTO.Events;

// The events a member answered to, and what they answered.
public sealed class EventsMemberData(IDbContextFactory<BotDbContext> dbFactory) : IMemberData
{
    public string Name => "Events";

    public async Task<long> CountAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await Mine(db, guildId, userId).LongCountAsync();
    }

    public async Task WriteAsync(ulong guildId, ulong userId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        json.WriteStartArray();
        await foreach (var (rsvp, held) in Mine(db, guildId, userId).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteNumber("event", held.Id);
            json.WriteString("title", held.Title);
            if (held.StartsAt is { } starts)
                json.WriteString("startsAt", starts);
            else
                json.WriteNull("startsAt");
            json.WriteString("state", held.State);
            json.WriteString("answer", rsvp.Status);
            json.WriteString("at", rsvp.At);
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    // Ordered before the tuple is made: Postgres can't be asked to order by a tuple's member.
    private static IQueryable<(EventRsvp Rsvp, Event Event)> Mine(BotDbContext db, ulong guildId, ulong userId)
        => db.EventRsvps.AsNoTracking().Where(r => r.UserId == userId)
            .Join(db.Events.AsNoTracking().Where(e => e.GuildId == guildId), r => r.EventId, e => e.Id, (r, e) => new { Rsvp = r, Event = e })
            .OrderBy(x => x.Event.Id)
            .Select(x => new ValueTuple<EventRsvp, Event>(x.Rsvp, x.Event));
}
