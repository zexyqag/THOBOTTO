namespace THOBOTTO.Access;

public static class AccessStartup
{
    public static IServiceCollection AddAccess(this IServiceCollection services) => services.AddSingleton<AccessControl>();
}
