using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Archive;
using THOBOTTO.Data;
using THOBOTTO.Discord;

namespace THOBOTTO.Events;

// Every event of the server with its RSVPs and the times voted on, and every series, for the archive's export.
public sealed class EventsExport(IDbContextFactory<BotDbContext> dbFactory) : IExportSource
{
    private const int Batch = 500;

    public string Name => "events";

    public async Task WriteAsync(ulong guildId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        json.WriteStartObject();
        json.WriteStartArray("events");
        long after = 0;
        while (true)
        {
            var events = await db.Events.AsNoTracking().Where(e => e.GuildId == guildId && e.Id > after).OrderBy(e => e.Id).Take(Batch).ToListAsync();
            if (events.Count == 0)
                break;
            var ids = events.Select(e => e.Id).ToList();
            var rsvps = (await db.EventRsvps.AsNoTracking().Where(r => ids.Contains(r.EventId)).OrderBy(r => r.At).ToListAsync()).ToLookup(r => r.EventId);
            var options = (await db.EventTimeOptions.AsNoTracking().Where(o => ids.Contains(o.EventId)).OrderBy(o => o.Id).ToListAsync()).ToLookup(o => o.EventId);
            var optionIds = options.SelectMany(o => o).Select(o => o.Id).ToList();
            var votes = (await db.EventTimeVotes.AsNoTracking().Where(v => optionIds.Contains(v.OptionId)).ToListAsync()).ToLookup(v => v.OptionId);
            foreach (var e in events)
            {
                json.WriteStartObject();
                json.WriteNumber("id", e.Id);
                json.WriteId("channel", e.ChannelId);
                json.WriteId("message", e.MessageId);
                json.WriteId("creator", e.CreatorId);
                json.WriteString("title", e.Title);
                json.WriteString("description", e.Description);
                json.WriteId("pingRole", e.PingRoleId);
                json.WriteTime("startsAt", e.StartsAt);
                json.WriteTime("pollClosesAt", e.PollClosesAt);
                json.WriteBoolean("allowProposals", e.AllowProposals);
                json.WriteString("state", e.State);
                json.WriteNumber("series", e.SeriesId);
                json.WriteNumber("game", e.GameId);
                json.WriteString("mode", e.Mode);
                json.WriteString("voiceMode", e.VoiceMode);
                json.WriteId("voiceChannel", e.VoiceChannelId);
                json.WriteBoolean("wantsDiscordEvent", e.WantsDiscordEvent);
                json.WriteId("discordEvent", e.DiscordEventId);
                json.WriteNumber("capacity", e.Capacity);
                json.WriteNumber("firstPart", e.FirstPartId);
                json.WriteString("createdAt", e.CreatedAt);
                json.WriteStartArray("rsvps");
                foreach (var rsvp in rsvps[e.Id])
                {
                    json.WriteStartObject();
                    json.WriteId("member", rsvp.UserId);
                    json.WriteString("status", rsvp.Status);
                    json.WriteString("at", rsvp.At);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteStartArray("timeOptions");
                foreach (var option in options[e.Id])
                {
                    json.WriteStartObject();
                    json.WriteNumber("id", option.Id);
                    json.WriteString("startsAt", option.StartsAt);
                    json.WriteId("proposedBy", option.ProposedById);
                    json.WriteString("createdAt", option.CreatedAt);
                    json.WriteStartArray("votes");
                    foreach (var vote in votes[option.Id])
                        json.WriteIdValue(vote.UserId);
                    json.WriteEndArray();
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            await json.FlushAsync();
            after = events[^1].Id;
        }
        json.WriteEndArray();
        json.WriteStartArray("series");
        await foreach (var series in db.EventSeries.AsNoTracking().Where(s => s.GuildId == guildId).OrderBy(s => s.Id).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteNumber("id", series.Id);
            json.WriteId("channel", series.ChannelId);
            json.WriteId("creator", series.CreatorId);
            json.WriteString("title", series.Title);
            json.WriteString("description", series.Description);
            json.WriteId("pingRole", series.PingRoleId);
            json.WriteStartArray("days");
            foreach (var day in series.Days)
                json.WriteNumberValue(day);
            json.WriteEndArray();
            json.WriteNumber("timeOfDay", series.TimeOfDay);
            json.WriteString("zone", series.Zone);
            json.WriteNumber("openDaysAhead", series.OpenDaysAhead);
            json.WriteString("voiceMode", series.VoiceMode);
            json.WriteBoolean("wantsDiscordEvent", series.WantsDiscordEvent);
            json.WriteNumber("capacity", series.Capacity);
            json.WriteNumber("game", series.GameId);
            json.WriteString("mode", series.Mode);
            json.WriteBoolean("active", series.Active);
            json.WriteString("createdAt", series.CreatedAt);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }
}
