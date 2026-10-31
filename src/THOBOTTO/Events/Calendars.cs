using System.Globalization;

using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;

using Microsoft.EntityFrameworkCore;

using NetCord.Gateway;

using THOBOTTO.Data;
using THOBOTTO.Modules;
using THOBOTTO.Notifications;
using THOBOTTO.Panel;

namespace THOBOTTO.Events;

// Events in people's own calendars: a link per server and per member that calendar apps subscribe to, and one event
// to add by hand (Google Calendar, or a file the others open).
public sealed class Calendars(
    IDbContextFactory<BotDbContext> dbFactory,
    SettingsStore settings,
    GatewayClient gateway,
    PanelAccess access,
    IEnumerable<IEventKind> kinds,
    TimeProvider time)
{
    // How far back a feed reaches, so last week's still shows.
    private static readonly TimeSpan Past = TimeSpan.FromDays(30);

    // A link's calendar, or null for a token nobody has.
    public async Task<string?> FeedAsync(string token)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.CalendarLinks.AsNoTracking().FirstOrDefaultAsync(l => l.Token == token) is not { } link)
            return null;
        var since = time.GetUtcNow() - Past;
        List<Shown> shown = [];
        if (link.GuildId is { } guildId)
        {
            shown.AddRange((await db.Events.AsNoTracking().Where(e => e.GuildId == guildId && e.StartsAt >= since).ToListAsync()).Select(e => new Shown(e)));
            return await IcsAsync($"{GuildName(guildId)} events", shown);
        }
        var userId = link.UserId!.Value;
        foreach (var guild in gateway.Cache.Guilds.Values.ToList())
        {
            var choice = CalendarLinks.ChoiceIn(link, guild.Id);
            if (choice == CalendarChoices.Nothing || await access.InAsync(guild.Id, userId) is not var (_, member))
                continue;
            var events = await db.Events.AsNoTracking().Where(e => e.GuildId == guild.Id && e.StartsAt >= since).ToListAsync();
            var ids = events.Select(e => e.Id).ToList();
            // What they said, marked on each (in, or maybe), with their reminder on what they're in for.
            var said = await db.EventRsvps.Where(r => r.UserId == userId && ids.Contains(r.EventId) && (r.Status == RsvpStatuses.In || r.Status == RsvpStatuses.Maybe))
                .ToDictionaryAsync(r => r.EventId, r => r.Status);
            if (choice == CalendarChoices.Mine)
                events = [.. events.Where(e => said.ContainsKey(e.Id))];
            else if (choice == CalendarChoices.Games)
            {
                HashSet<long> followed = [];
                foreach (var kind in kinds)
                    followed.UnionWith(await kind.FollowedAsync(guild, member));
                events = [.. events.Where(e => e.GameId is not { } game || followed.Contains(game))];
            }
            shown.AddRange(events.Select(e => said.GetValueOrDefault(e.Id) is var status && status == RsvpStatuses.In
                ? new Shown(e, guild.Name, "✅ ", link.ReminderMinutes)
                : new Shown(e, guild.Name, status == RsvpStatuses.Maybe ? "🤔 " : null)));
        }
        return await IcsAsync($"{gateway.Cache.User?.Username ?? "Discord"} events", shown);
    }

    // One event as a calendar file, when the token is its server's link or a member's in that server.
    public async Task<(Event Event, string Ics, string Google)?> OneAsync(string token, long eventId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.CalendarLinks.AsNoTracking().FirstOrDefaultAsync(l => l.Token == token) is not { } link
            || await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventId) is not { StartsAt: { } start } e
            || (link.GuildId is { } guildId ? guildId != e.GuildId : await access.InAsync(e.GuildId, link.UserId!.Value) is null))
            return null;
        var end = start + (await settings.GetAsync<EventRules>(e.GuildId, EventBoard.ModuleId)).LengthOf(e);
        var details = await DetailsAsync(e, null);
        var google = "https://calendar.google.com/calendar/render?action=TEMPLATE"
            + $"&text={Uri.EscapeDataString(e.Title)}&dates={Stamp(start)}/{Stamp(end)}&details={Uri.EscapeDataString(details)}";
        return (e, await IcsAsync(e.Title, [new(e)]), google);
    }

    // An event in a calendar: on whose server (a member's own), marked with what they said, and their reminder.
    private sealed record Shown(Event Event, string? Server = null, string? Mark = null, int? RemindMinutes = null);

    private async Task<string> IcsAsync(string name, IReadOnlyList<Shown> shown)
    {
        var rules = new Dictionary<ulong, EventRules>();
        var written = new List<IcsEvent>();
        foreach (var (e, server, mark, remind) in shown.Where(s => s.Event.StartsAt is not null).OrderBy(s => s.Event.StartsAt))
        {
            if (!rules.TryGetValue(e.GuildId, out var r))
                rules[e.GuildId] = r = await settings.GetAsync<EventRules>(e.GuildId, EventBoard.ModuleId);
            var cancelled = e.State == EventStates.Cancelled;
            written.Add(new($"event-{e.Id}-{e.GuildId}@thobotto", e.StartsAt!.Value, r.LengthOf(e), mark + e.Title, await DetailsAsync(e, server), Link(e), cancelled,
                cancelled ? null : remind, e.Title));
        }
        return Ics(name, written, time.GetUtcNow());
    }

    // What a calendar shows of an event; RemindMinutes: an alarm that long before, saying AlarmText.
    public sealed record IcsEvent(string Uid, DateTimeOffset Start, TimeSpan Length, string Summary, string Description, string? Url, bool Cancelled, int? RemindMinutes, string AlarmText);

    // The calendar file, refreshed hourly by the apps that subscribe to it.
    public static string Ics(string name, IEnumerable<IcsEvent> events, DateTimeOffset now)
    {
        var calendar = new Ical.Net.Calendar { ProductId = "-//THOBOTTO//Events//EN", Method = "PUBLISH" };
        calendar.AddProperty("X-WR-CALNAME", name);
        calendar.AddProperty(new CalendarProperty("REFRESH-INTERVAL", "PT1H") { Parameters = { new CalendarParameter("VALUE", "DURATION") } });
        calendar.AddProperty("X-PUBLISHED-TTL", "PT1H");
        foreach (var e in events)
        {
            var written = new CalendarEvent
            {
                Uid = e.Uid,
                DtStamp = new CalDateTime(now.UtcDateTime),
                DtStart = new CalDateTime(e.Start.UtcDateTime),
                DtEnd = new CalDateTime((e.Start + e.Length).UtcDateTime),
                Summary = e.Summary,
                Description = e.Description,
                Url = e.Url is null ? null : new Uri(e.Url),
                Status = e.Cancelled ? EventStatus.Cancelled : EventStatus.Confirmed,
            };
            if (e.RemindMinutes is { } minutes)
                written.Alarms.Add(new Alarm { Action = AlarmAction.Display, Description = e.AlarmText, Trigger = new Trigger(Duration.FromMinutes(-minutes)) });
            calendar.Events.Add(written);
        }
        return new CalendarSerializer().SerializeToString(calendar)!;
    }

    private async Task<string> DetailsAsync(Event e, string? server)
    {
        List<string> lines = [];
        if (e.GameId is { } gameId)
        {
            foreach (var kind in kinds)
            {
                if ((await kind.NamesAsync(e.GuildId)).TryGetValue(gameId, out var game))
                    lines.Add($"Game: {game}");
            }
        }
        if (e.Description is { } description)
            lines.Add(description);
        if (server is not null)
            lines.Add($"On {server}");
        if (Link(e) is { } link)
            lines.Add(link);
        return string.Join('\n', lines);
    }

    private static string? Link(Event e) => e.MessageId is { } message ? Notifier.Link(e.GuildId, e.ChannelId, message) : null;

    private string GuildName(ulong guildId) => gateway.Cache.Guilds.TryGetValue(guildId, out var guild) ? guild.Name : "Discord";

    public static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
}
