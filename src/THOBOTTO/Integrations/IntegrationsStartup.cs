namespace THOBOTTO.Integrations;

public static class IntegrationsStartup
{
    // Started before everything else: the others read its settings from the start.
    public static IServiceCollection AddIntegrations(this IServiceCollection services)
        => services
            .AddSingleton<IntegrationStore>()
            .AddHostedService(s => s.GetRequiredService<IntegrationStore>());
}
