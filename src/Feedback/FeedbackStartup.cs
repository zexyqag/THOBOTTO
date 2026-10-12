namespace THOBOTTO.Feedback;

public static class FeedbackStartup
{
    public static IServiceCollection AddFeedback(this IServiceCollection services) => services.AddSingleton<FeedbackBox>();
}
