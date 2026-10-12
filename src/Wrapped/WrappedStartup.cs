using THOBOTTO.Panel;

namespace THOBOTTO.Wrapped;

public static class WrappedStartup
{
    public static IServiceCollection AddWrapped(this IServiceCollection services)
        => services
            .AddSingleton<GenreBook>()
            .AddSingleton<WrappedStats>()
            .AddSingleton<DiscordStats>()
            .AddSingleton<WrappedService>()
            .AddSingleton<WrappedNames>()
            .AddHostedService<WrappedPoster>()
            .AddSingleton<VoiceSessions>()
            .AddHostedService(s => s.GetRequiredService<VoiceSessions>())
            .AddSingleton(new GuildNavEntry("wrapped", "Wrapped", 40, Module: WrappedPoster.ModuleId));
}
