using THOBOTTO.Archive;
using THOBOTTO.Members;

namespace THOBOTTO.Fame;

public static class FameStartup
{
    public static IServiceCollection AddFame(this IServiceCollection services)
        => services
            .AddSingleton<HallOfFame>()
            .AddSingleton<IExportSource, FameExport>()
            .AddSingleton<IMemberData, FameMemberData>();
}
