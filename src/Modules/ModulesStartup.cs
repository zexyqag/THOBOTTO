namespace THOBOTTO.Modules;

public static class ModulesStartup
{
    public static IServiceCollection AddModules(this IServiceCollection services)
        => services
            .AddSingleton<ModuleState>()
            .AddSingleton<SettingsStore>()
            .AddSingleton<SettingsPages>();
}
