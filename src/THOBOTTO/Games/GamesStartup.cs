using THOBOTTO.Archive;
using THOBOTTO.Events;
using THOBOTTO.Members;
using THOBOTTO.Modules;
using THOBOTTO.Notifications;
using THOBOTTO.Overview;
using THOBOTTO.Panel;

namespace THOBOTTO.Games;

public static class GamesStartup
{
    public static IServiceCollection AddGames(this IServiceCollection services)
        => services
            .AddSingleton<IPostingSource, GamePostings>()
            .AddSingleton<IExportSource, GamesExport>()
            .AddSingleton(new GuildNavEntry("games", "Games", GuildNavGroup.Community, 15, Module: GameDirectory.ModuleId))
            .AddSingleton<IWidgetKind, GameRolesWidget>()
            .AddSingleton<GameDirectory>()
            .AddSingleton<INotificationTopicSource>(s => s.GetRequiredService<GameDirectory>())
            .AddSingleton<IEventDecorator>(s => s.GetRequiredService<GameDirectory>())
            .AddHostedService(s => s.GetRequiredService<GameDirectory>())
            .AddSingleton<GameOnboarding>()
            .AddHostedService(s => s.GetRequiredService<GameOnboarding>())
            .AddSingleton<ISettingsSummary, GameOnboardingSummary>()
            .AddSingleton<GameSessions>()
            .AddSingleton<IEventKind>(s => s.GetRequiredService<GameSessions>())
            .AddSingleton<GroupProposals>()
            .AddHostedService<GroupProposalSweep>()
            .AddSingleton<GroupLineups>()
            .AddSingleton<IMemberData, GroupProposalsMemberData>();
}
