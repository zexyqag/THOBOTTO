using THOBOTTO.Events;
using THOBOTTO.Notifications;

namespace THOBOTTO.Games;

public static class GamesStartup
{
    public static IServiceCollection AddGames(this IServiceCollection services)
        => services
            .AddSingleton<GameDirectory>()
            .AddSingleton<INotificationTopicSource>(s => s.GetRequiredService<GameDirectory>())
            .AddSingleton<IEventDecorator>(s => s.GetRequiredService<GameDirectory>())
            .AddHostedService(s => s.GetRequiredService<GameDirectory>())
            .AddSingleton<GameSessions>()
            .AddSingleton<IEventKind>(s => s.GetRequiredService<GameSessions>());
}
