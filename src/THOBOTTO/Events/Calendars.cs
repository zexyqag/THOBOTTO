using System.Globalization;
using System.Text;

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
        List<(Event Event, string? Server, string? Mark)> shown = [];
        if (link.GuildId is { } guildId)
        {
            shown.AddRange((await db.Events.AsNoTracking().Where(e => e.GuildId == guildId && e.StartsAt >= since).ToListAsync()).Select(e => (e, (string?)null, (string?)null)));
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
            // What they said, marked on each: in, or maybe.
            var said = await db.EventRsvps.Where(r => r.UserId == userId && ids.Contains(r.EventId) && (r.Status == RsvpStatuses.In || r.Status == RsvpStatuses.Maybe))
                .ToDictionaryAsync(r => r.EventId, r => r.Status == RsvpStatuses.In ? "✅ " : "🤔 ");
            if (choice == CalendarChoices.Mine)
                events = [.. events.Where(e => said.ContainsKey(e.Id))];
            else if (choice == CalendarChoices.Games)
            {
                HashSet<long> followed = [];
                foreach (var kind in kinds)
                    followed.UnionWith(await kind.FollowedAsync(guild, member));
                events = [.. events.Where(e => e.GameId is not { } game || followed.Contains(game))];
            }
            shown.AddRange(events.Select(e => (e, (string?)guild.Name, said.GetValueOrDefault(e.Id))));
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
        return (e, await IcsAsync(e.Title, [(e, null, null)]), google);
    }

    private async Task<string> IcsAsync(string name, IReadOnlyList<(Event Event, string? Server, string? Mark)> shown)
    {
        var text = new StringBuilder();
        void Line(string line) => text.Append(Fold(line)).Append("\r\n");
        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line("PRODID:-//THOBOTTO//Events//EN");
        Line("CALSCALE:GREGORIAN");
        Line("METHOD:PUBLISH");
        Line($"X-WR-CALNAME:{Escape(name)}");
        Line("REFRESH-INTERVAL;VALUE=DURATION:PT1H");
        Line("X-PUBLISHED-TTL:PT1H");
        var now = Stamp(time.GetUtcNow());
        var rules = new Dictionary<ulong, EventRules>();
        foreach (var (e, server, mark) in shown.Where(s => s.Event.StartsAt is not null).OrderBy(s => s.Event.StartsAt))
        {
            if (!rules.TryGetValue(e.GuildId, out var r))
                rules[e.GuildId] = r = await settings.GetAsync<EventRules>(e.GuildId, EventBoard.ModuleId);
            var start = e.StartsAt!.Value;
            Line("BEGIN:VEVENT");
            Line($"UID:event-{e.Id}-{e.GuildId}@thobotto");
            Line($"DTSTAMP:{now}");
            Line($"DTSTART:{Stamp(start)}");
            Line($"DTEND:{Stamp(start + r.LengthOf(e))}");
            Line($"SUMMARY:{Escape(mark + e.Title)}");
            Line($"DESCRIPTION:{Escape(await DetailsAsync(e, server))}");
            if (Link(e) is { } link)
                Line($"URL:{link}");
            Line($"STATUS:{(e.State == EventStates.Cancelled ? "CANCELLED" : "CONFIRMED")}");
            Line("END:VEVENT");
        }
        Line("END:VCALENDAR");
        return text.ToString();
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

    // Text as iCalendar wants it: backslashes, semicolons, commas and line breaks escaped.
    public static string Escape(string text)
        => text.Replace(@"\", @"\\").Replace(";", @"\;").Replace(",", @"\,").Replace("\r\n", "\n").Replace("\n", @"\n");

    // Lines past 75 bytes go on in the next, which starts with a space.
    public static string Fold(string line)
    {
        var folded = new StringBuilder();
        var bytes = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (bytes + size > 75)
            {
                folded.Append("\r\n ");
                bytes = 1;
            }
            folded.Append(rune.ToString());
            bytes += size;
        }
        return folded.ToString();
    }
}
