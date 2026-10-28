using THOBOTTO.Archive;
using THOBOTTO.Events;
using THOBOTTO.Notifications;

using THOBOTTO.Panel;

namespace THOBOTTO.Games;

public static class GamesStartup
{
    public static IServiceCollection AddGames(this IServiceCollection services)
        => services
            .AddSingleton<IExportSource, GamesExport>()
            .AddSingleton(new GuildNavEntry("games", "Games", GuildNavGroup.Community, 15, Module: GameDirectory.ModuleId))
            .AddSingleton<GameDirectory>()
            .AddSingleton<INotificationTopicSource>(s => s.GetRequiredService<GameDirectory>())
            .AddSingleton<IEventDecorator>(s => s.GetRequiredService<GameDirectory>())
            .AddHostedService(s => s.GetRequiredService<GameDirectory>())
            .AddSingleton<GameSessions>()
            .AddSingleton<IEventKind>(s => s.GetRequiredService<GameSessions>());
}
