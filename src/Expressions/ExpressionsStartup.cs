namespace THOBOTTO.Expressions;

public static class ExpressionsStartup
{
    public static IServiceCollection AddExpressions(this IServiceCollection services)
        => services
            .AddSingleton<ExpressionShelf>()
            .AddHostedService(s => s.GetRequiredService<ExpressionShelf>());
}
