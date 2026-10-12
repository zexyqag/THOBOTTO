namespace THOBOTTO.Expressions;

public static class ExpressionsStartup
{
    public static IServiceCollection AddExpressions(this IServiceCollection services)
    {
        services.AddHttpClient(nameof(ExpressionShelf)).AddAsKeyed(ServiceLifetime.Singleton);
        return services
            .AddSingleton<ExpressionShelf>()
            .AddHostedService(s => s.GetRequiredService<ExpressionShelf>());
    }
}
