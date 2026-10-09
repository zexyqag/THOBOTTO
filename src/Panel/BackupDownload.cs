using System.Text;

using THOBOTTO.Access;
using THOBOTTO.Backups;

namespace THOBOTTO.Panel;

// A server's backup as a file: made now, or one the bot kept. For those who manage permissions there.
public static class BackupDownload
{
    public static async Task<IResult> ServeAsync(HttpContext context, ulong guildId, long? stored, PanelAccess access, BackupMaker maker, BackupVault vault)
    {
        if (PanelAccess.UserId(context.User) is not { } userId || await access.InAsync(guildId, userId) is not { } found
            || !await access.CanAsync(found.Guild, found.Member, BotPermissions.ManagePermissions))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (stored is { } id)
        {
            if (await vault.FindAsync(guildId, id) is not { } kept || ServerBackup.Parse(kept.Json).Backup is not { } old)
                return Results.NotFound();
            return Results.File(Encoding.UTF8.GetBytes(kept.Json), "application/json", old.FileName);
        }
        var backup = await maker.ExportAsync(found.Guild);
        return Results.File(Encoding.UTF8.GetBytes(backup.ToJson()), "application/json", backup.FileName);
    }
}
