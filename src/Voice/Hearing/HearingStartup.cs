using THOBOTTO.Helpers;

namespace THOBOTTO.Voice.Hearing;

public static class HearingStartup
{
    public static IServiceCollection AddHearing(this IServiceCollection services)
    {
        // Model downloads of hundreds of megabytes.
        services.AddHttpClient(nameof(ParakeetSpeech), c => c.Timeout = TimeSpan.FromMinutes(30)).AddAsKeyed(ServiceLifetime.Singleton);
        return services
            .AddSingleton<LocalWhisper>()
            .AddSingleton<ParakeetSpeech>()
            .AddSingleton<SpeechToText>()
            .AddSingleton<ISpeechToText>(s => s.GetRequiredService<SpeechToText>())
            .AddHostedService(s => s.GetRequiredService<SpeechToText>())
            .AddSingleton<VoiceEars>()
            .AddHostedService(s => s.GetRequiredService<VoiceEars>())
            .AddSingleton<IHelperAware>(s => s.GetRequiredService<VoiceEars>())
            .AddSingleton<VoiceTranscript>();
    }
}
