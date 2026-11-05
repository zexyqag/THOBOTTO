using THOBOTTO.Helpers;
using THOBOTTO.Modules;

namespace THOBOTTO.Voice.Channels;

public static class VoiceChannelsStartup
{
    public static IServiceCollection AddVoiceChannels(this IServiceCollection services)
        => services
            .AddSingleton<IPostingSource, VoicePostings>()
            .AddSingleton<DynamicVoice>()
            .AddHostedService(s => s.GetRequiredService<DynamicVoice>())
            .AddSingleton<IPassingChannels>(s => s.GetRequiredService<DynamicVoice>())
            .AddSingleton<ChannelNamer>()
            .AddHostedService(s => s.GetRequiredService<ChannelNamer>());
}
