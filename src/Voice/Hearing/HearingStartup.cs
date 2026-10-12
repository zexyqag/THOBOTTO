using THOBOTTO.Helpers;

namespace THOBOTTO.Voice.Hearing;

public static class HearingStartup
{
    public static IServiceCollection AddHearing(this IServiceCollection services)
        => services
            .AddSingleton<LocalWhisper>()
            .AddSingleton<ParakeetSpeech>()
            .AddSingleton<SpeechToText>()
            .AddSingleton<ISpeechToText>(s => s.GetRequiredService<SpeechToText>())
            .AddSingleton<VoiceEars>()
            .AddHostedService(s => s.GetRequiredService<VoiceEars>())
            .AddSingleton<IHelperAware>(s => s.GetRequiredService<VoiceEars>())
            .AddSingleton<VoiceTranscript>();
}
