namespace THOBOTTO.Members;

public static class MembersStartup
{
    public static IServiceCollection AddMembers(this IServiceCollection services) => services.AddSingleton<TimeZones>();
}
