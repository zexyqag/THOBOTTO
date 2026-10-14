using THOBOTTO.Members;

namespace THOBOTTO.Gate;

public static class GateStartup
{
    public static IServiceCollection AddGate(this IServiceCollection services)
        => services
            .AddSingleton<Gatekeeper>()
            .AddHostedService(s => s.GetRequiredService<Gatekeeper>())
            .AddSingleton<IMemberLeft>(s => s.GetRequiredService<Gatekeeper>());
}
