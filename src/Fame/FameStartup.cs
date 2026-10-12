namespace THOBOTTO.Fame;

public static class FameStartup
{
    public static IServiceCollection AddFame(this IServiceCollection services) => services.AddSingleton<HallOfFame>();
}
