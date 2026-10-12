namespace THOBOTTO.Panel.Pages;

public static class PagesStartup
{
    public static IServiceCollection AddPages(this IServiceCollection services)
        => services
            .AddSingleton<IntegrationStatus>()
            .AddSingleton(new BotNavEntry("/", "Home", 10))
            .AddSingleton(new BotNavEntry("/status", "Status", 20))
            .AddSingleton(new BotNavEntry("/integrations", "Integrations", 40))
            .AddSingleton(new BotNavEntry("/debug", "Debug", 60));
}
