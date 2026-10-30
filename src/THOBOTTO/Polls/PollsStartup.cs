using THOBOTTO.Happenings;

namespace THOBOTTO.Polls;

public static class PollsStartup
{
    public static IServiceCollection AddPolls(this IServiceCollection services)
        => services
            .AddSingleton<ActivityPolls>()
            .AddSingleton(ActivityPolls.Happening)
            .AddHostedService(s => s.GetRequiredService<ActivityPolls>());
}
