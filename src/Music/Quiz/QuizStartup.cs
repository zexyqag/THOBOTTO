namespace THOBOTTO.Music.Quiz;

public static class QuizStartup
{
    public static IServiceCollection AddQuiz(this IServiceCollection services)
        => services
            .AddSingleton<QuizRooms>()
            .AddHostedService(s => s.GetRequiredService<QuizRooms>());
}
