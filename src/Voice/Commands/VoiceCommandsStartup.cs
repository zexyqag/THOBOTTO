using THOBOTTO.Helpers;

namespace THOBOTTO.Voice.Commands;

public static class VoiceCommandsStartup
{
    public static IServiceCollection AddVoiceCommands(this IServiceCollection services)
    {
        services.AddHttpClient(nameof(Understanding), c => c.Timeout = TimeSpan.FromSeconds(20)).AddAsKeyed(ServiceLifetime.Singleton);
        return services
            .AddHostedService<VoiceCommands>()
            .AddSingleton<Understanding>()
            .AddSingleton<PeopleFinder>()
            .AddSingleton<VoiceQuestions>()
            .AddSingleton<IHelperAware>(s => s.GetRequiredService<VoiceQuestions>());
    }
}
