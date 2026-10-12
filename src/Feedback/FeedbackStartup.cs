using THOBOTTO.Panel;

namespace THOBOTTO.Feedback;

public static class FeedbackStartup
{
    public static IServiceCollection AddFeedback(this IServiceCollection services)
        => services
            .AddSingleton<FeedbackBox>()
            .AddSingleton(new BotNavEntry("/feedback", "Feedback", 50));
}
