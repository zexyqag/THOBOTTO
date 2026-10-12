namespace THOBOTTO.Panel.Pages;

public static class PagesStartup
{
    public static IServiceCollection AddPages(this IServiceCollection services)
        => services.AddSingleton<IntegrationStatus>();
}
