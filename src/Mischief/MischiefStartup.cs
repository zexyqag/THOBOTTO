using THOBOTTO.Archive;
using THOBOTTO.Members;
using THOBOTTO.Voice.Commands;

namespace THOBOTTO.Mischief;

public static class MischiefStartup
{
    public static IServiceCollection AddMischief(this IServiceCollection services)
        => services
            .AddSingleton<IVoiceActions, MischiefVoiceActions>()
            .AddSingleton<IExportSource, MischiefExport>()
            .AddSingleton<PaintRoles>()
            .AddSingleton<MischiefActions>()
            .AddHostedService(s => s.GetRequiredService<PaintRoles>())
            .AddSingleton<IMemberData, RenamesMemberData>();
}
