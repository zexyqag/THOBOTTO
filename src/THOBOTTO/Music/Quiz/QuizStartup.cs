using THOBOTTO.Happenings;

namespace THOBOTTO.Music.Quiz;

public static class QuizStartup
{
    public static IServiceCollection AddQuiz(this IServiceCollection services)
        => services
            .AddSingleton<QuizRooms>()
            .AddSingleton(QuizRooms.Happening)
            .AddHostedService(s => s.GetRequiredService<QuizRooms>());
}
