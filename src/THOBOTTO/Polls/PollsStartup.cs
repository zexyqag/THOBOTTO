namespace THOBOTTO.Polls;

public static class PollsStartup
{
    public static IServiceCollection AddPolls(this IServiceCollection services)
        => services
            .AddSingleton<ActivityPolls>()
            .AddHostedService(s => s.GetRequiredService<ActivityPolls>());
}
