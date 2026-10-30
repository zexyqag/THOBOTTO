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
            .AddSingleton(EventBoard.EventHappening)
            .AddSingleton(EventBoard.SessionHappening)
            .AddHostedService(s => s.GetRequiredService<EventBoard>())
            .AddSingleton<IMemberData, EventsMemberData>()
            .AddSingleton(new GuildNavEntry("events", "Events", GuildNavGroup.Community, 10, Module: EventBoard.ModuleId));
}
