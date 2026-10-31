using THOBOTTO.Events;
using THOBOTTO.Members;
using THOBOTTO.Overview;
using THOBOTTO.Panel;

namespace THOBOTTO.GameServers;

public static class GameServersStartup
{
    public static IServiceCollection AddGameServers(this IServiceCollection services)
    {
        services.AddOptions<GameDigOptions>().BindConfiguration("GameDig");
        // Palworld's REST API; the whole command's time is ServerControl's.
        services.AddHttpClient(nameof(ServerControl), c => c.Timeout = Timeout.InfiniteTimeSpan).AddAsKeyed(ServiceLifetime.Singleton);
        return services
            .AddSingleton<GameDig>()
            .AddSingleton<ServerWatch>()
            .AddSingleton<ServerDetails>()
            .AddSingleton<ServerControl>()
            .AddSingleton<IEventDecorator>(s => s.GetRequiredService<ServerControl>())
            .AddSingleton<ServerWhitelist>()
            .AddHostedService(s => s.GetRequiredService<ServerWhitelist>())
            .AddSingleton<IMemberLeft>(s => s.GetRequiredService<ServerWhitelist>())
            .AddSingleton<IMemberData, WhitelistMemberData>()
            .AddSingleton<ChatBridge>()
            .AddHostedService(s => s.GetRequiredService<ChatBridge>())
            .AddSingleton(new GuildNavEntry("servers", "Game servers", GuildNavGroup.Community, 60, Module: ServerWatch.ModuleId))
            .AddSingleton<IWidgetKind, ServerListWidget>()
            .AddHostedService(s => s.GetRequiredService<ServerWatch>());
    }
}
