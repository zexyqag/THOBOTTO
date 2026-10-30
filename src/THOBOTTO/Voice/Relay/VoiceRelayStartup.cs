namespace THOBOTTO.Voice.Relay;

public static class VoiceRelayStartup
{
    public static IServiceCollection AddVoiceRelay(this IServiceCollection services)
        => services
            .AddSingleton<VoiceRelay>()
            .AddSingleton<RelayTrust>()
            .AddHostedService(s => s.GetRequiredService<RelayTrust>())
            .AddHostedService(s => s.GetRequiredService<VoiceRelay>());
}
