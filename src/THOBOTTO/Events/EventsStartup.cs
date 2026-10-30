using System.Net;
using System.Text;

using THOBOTTO.Archive;
using THOBOTTO.Happenings;
using THOBOTTO.Members;
using THOBOTTO.Overview;
using THOBOTTO.Panel;
using THOBOTTO.Voice.Commands;
using THOBOTTO.Wrapped;

namespace THOBOTTO.Events;

public static class EventsStartup
{
    public static IServiceCollection AddEvents(this IServiceCollection services)
        => services
            .AddSingleton<IGuildTimeZone, GuildTimeZone>()
            .AddSingleton<IWidgetKind, UpcomingEventsWidget>()
            .AddSingleton<IVoiceActions, EventVoiceActions>()
            .AddSingleton<IExportSource, EventsExport>()
            .AddSingleton<IWrappedSection, EventsWrapped>()
            .AddSingleton<EventBoard>()
            .AddSingleton<CalendarLinks>()
            .AddSingleton<Calendars>()
            .AddSingleton(EventBoard.EventHappening)
            .AddSingleton(EventBoard.SessionHappening)
            .AddHostedService(s => s.GetRequiredService<EventBoard>())
            .AddSingleton<IMemberData, EventsMemberData>()
            .AddSingleton(new GuildNavEntry("events", "Events", GuildNavGroup.Community, 10, Module: EventBoard.ModuleId));
    // Calendar links: no sign-in (calendar apps can't); the token is the secret.
    public static WebApplication MapEvents(this WebApplication app)
    {
        app.MapGet("/calendar/{token}.ics", async (string token, Calendars calendars)
            => await calendars.FeedAsync(token) is { } ics ? Results.Text(ics, "text/calendar; charset=utf-8") : Results.NotFound());
        app.MapGet("/calendar/{token}/{eventId:long}.ics", async (string token, long eventId, Calendars calendars)
            => await calendars.OneAsync(token, eventId) is { } one ? Results.File(Encoding.UTF8.GetBytes(one.Ics), "text/calendar; charset=utf-8", $"event-{eventId}.ics") : Results.NotFound());
        app.MapGet("/calendar/{token}/{eventId:long}", async (string token, long eventId, Calendars calendars) =>
        {
            if (await calendars.OneAsync(token, eventId) is not { } one)
                return Results.NotFound();
            var title = WebUtility.HtmlEncode(one.Event.Title);
            var start = one.Event.StartsAt!.Value;
            return Results.Content($$"""
                <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>{{title}}</title>
                <style>body{font-family:system-ui,sans-serif;max-width:32rem;margin:3rem auto;padding:0 1rem;line-height:1.5}a{display:block;margin:.6rem 0}</style></head>
                <body><h1>{{title}}</h1><p><time datetime="{{start.ToString("o")}}">{{start.UtcDateTime.ToString("yyyy-MM-dd HH:mm")}} UTC</time></p>
                <a href="{{WebUtility.HtmlEncode(one.Google)}}">Add to Google Calendar</a>
                <a href="/calendar/{{token}}/{{eventId}}.ics">Add to another calendar (Apple, Outlook and others)</a>
                <script>for(const t of document.querySelectorAll("time"))t.textContent=new Date(t.getAttribute("datetime")).toLocaleString()</script></body></html>
                """, "text/html; charset=utf-8");
        });
        return app;
    }
}
