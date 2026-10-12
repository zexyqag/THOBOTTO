namespace THOBOTTO.Voice.Relay;

public static class VoiceRelayStartup
{
    public static IServiceCollection AddVoiceRelay(this IServiceCollection services)
        => services
            .AddSingleton<VoiceRelay>()
            .AddHostedService(s => s.GetRequiredService<VoiceRelay>());
}
