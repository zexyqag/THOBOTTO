using THOBOTTO.Archive;
using THOBOTTO.Members;
using THOBOTTO.Overview;
using THOBOTTO.Panel;

namespace THOBOTTO.Wrapped;

public static class WrappedStartup
{
    public static IServiceCollection AddWrapped(this IServiceCollection services)
        => services
            .AddSingleton<IExportSource, WrappedExport>()
            .AddSingleton<IWrappedSection, WrappedMessages>()
            .AddSingleton<IWrappedSection, WrappedVoiceTime>()
            .AddSingleton<WrappedService>()
            .AddSingleton<IMemberLeft>(s => s.GetRequiredService<WrappedService>())
            .AddSingleton<WrappedNames>()
            .AddHostedService<WrappedPoster>()
            .AddSingleton<IWidgetKind, LatestWrappedWidget>()
            .AddSingleton<VoiceSessions>()
            .AddHostedService(s => s.GetRequiredService<VoiceSessions>())
            .AddSingleton<IMemberData, VoiceTimeMemberData>()
            .AddSingleton<IMemberData, WrappedNameMemberData>()
            .AddSingleton(new GuildNavEntry("wrapped", "Wrapped", GuildNavGroup.Community, 40, Module: WrappedPoster.ModuleId));
}
