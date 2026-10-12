using THOBOTTO.Panel;

namespace THOBOTTO.Backups;

public static class BackupsStartup
{
    public static IServiceCollection AddBackups(this IServiceCollection services)
        => services
            .AddSingleton<BackupMaker>()
            .AddSingleton<BackupRestorer>()
            .AddSingleton<BackupVault>()
            .AddHostedService(s => s.GetRequiredService<BackupVault>())
            .AddSingleton<PendingRestores>();

    public static WebApplication MapBackups(this WebApplication app)
    {
        app.MapGet("/g/{guildId}/backup/download", (HttpContext context, ulong guildId, long? stored, PanelAccess access, BackupMaker maker, BackupVault vault)
            => BackupDownload.ServeAsync(context, guildId, stored, access, maker, vault)).RequireAuthorization();
        return app;
    }
}
