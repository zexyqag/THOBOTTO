namespace THOBOTTO.GameServers;

public static class GameServersStartup
{
    public static IServiceCollection AddGameServers(this IServiceCollection services)
    {
        services.AddOptions<GameDigOptions>().BindConfiguration("GameDig");
        return services
            .AddSingleton<GameDig>()
            .AddSingleton<ServerBoardService>()
            .AddHostedService(s => s.GetRequiredService<ServerBoardService>());
    }
}
