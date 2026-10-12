using THOBOTTO.Members;
using THOBOTTO.Panel;
using THOBOTTO.Voice.Commands;

namespace THOBOTTO.Events;

public static class EventsStartup
{
    public static IServiceCollection AddEvents(this IServiceCollection services)
        => services
            .AddSingleton<IServerTimeZone, ServerTimeZone>()
            .AddSingleton<IVoiceActions, EventVoiceActions>()
            .AddSingleton<EventBoard>()
            .AddHostedService(s => s.GetRequiredService<EventBoard>())
            .AddSingleton(new GuildNavEntry("events", "Events", 10, Module: EventBoard.ModuleId));
}
