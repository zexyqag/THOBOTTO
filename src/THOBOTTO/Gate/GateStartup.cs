using THOBOTTO.Members;
using THOBOTTO.Modules;

namespace THOBOTTO.Gate;

public static class GateStartup
{
    public static IServiceCollection AddGate(this IServiceCollection services)
        => services
            .AddSingleton<Gatekeeper>()
            .AddSingleton<ISettingsSummary, GateSummary>()
            .AddSingleton<ISettingsSummary, GateAlertsInLogSummary>()
            .AddHostedService(s => s.GetRequiredService<Gatekeeper>())
            .AddSingleton<IMemberLeft>(s => s.GetRequiredService<Gatekeeper>());
}
