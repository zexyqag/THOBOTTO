using THOBOTTO.Panel;

namespace THOBOTTO.GameServers;

public static class GameServersStartup
{
    public static IServiceCollection AddGameServers(this IServiceCollection services)
    {
        services.AddOptions<GameDigOptions>().BindConfiguration("GameDig");
        return services
            .AddSingleton<GameDig>()
            .AddSingleton<ServerBoardService>()
            .AddSingleton(new GuildNavEntry("servers", "Game servers", GuildNavGroup.Community, 60, Module: ServerBoardService.ModuleId))
            .AddHostedService(s => s.GetRequiredService<ServerBoardService>());
    }
}
