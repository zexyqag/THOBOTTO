using THOBOTTO.Quiz;

namespace THOBOTTO.Tests;

public class QuizTests
{
    [Theory]
    [InlineData(0, 1000)]
    [InlineData(10, 750)]
    [InlineData(20, 500)]
    [InlineData(25, 500)]
    public void Quicker_right_answers_score_more(int seconds, int score)
        => Assert.Equal(score, QuizRooms.Score(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(20)));
}
