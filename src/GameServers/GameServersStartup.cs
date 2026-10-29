using THOBOTTO.Overview;
using THOBOTTO.Panel;

namespace THOBOTTO.GameServers;

public static class GameServersStartup
{
    public static IServiceCollection AddGameServers(this IServiceCollection services)
    {
        services.AddOptions<GameDigOptions>().BindConfiguration("GameDig");
        return services
            .AddSingleton<GameDig>()
            .AddSingleton<ServerWatch>()
            .AddSingleton(new GuildNavEntry("servers", "Game servers", GuildNavGroup.Community, 60, Module: ServerWatch.ModuleId))
            .AddSingleton<IWidgetKind, ServerListWidget>()
            .AddHostedService(s => s.GetRequiredService<ServerWatch>());
    }
}
