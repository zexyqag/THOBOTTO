using THOBOTTO.Modules;

namespace THOBOTTO.Voice.Lines;

public static class LinesStartup
{
    public static IServiceCollection AddLines(this IServiceCollection services)
        => services
            .AddSingleton<HelperLines>()
            .AddSingleton<IPriceSource, LinePrices>();
}
