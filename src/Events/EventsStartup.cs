using THOBOTTO.Archive;
using THOBOTTO.Members;
using THOBOTTO.Panel;
using THOBOTTO.Voice.Commands;

namespace THOBOTTO.Events;

public static class EventsStartup
{
    public static IServiceCollection AddEvents(this IServiceCollection services)
        => services
            .AddSingleton<IGuildTimeZone, GuildTimeZone>()
            .AddSingleton<IVoiceActions, EventVoiceActions>()
            .AddSingleton<IExportSource, EventsExport>()
            .AddSingleton<EventBoard>()
            .AddHostedService(s => s.GetRequiredService<EventBoard>())
            .AddSingleton(new GuildNavEntry("events", "Events", GuildNavGroup.Community, 10, Module: EventBoard.ModuleId));
}
