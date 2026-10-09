using System.Text;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Backups;

namespace THOBOTTO;

public sealed partial class SetupCommands
{
    [SubSlashCommand("backup", "The server's setup as a file, to keep or restore (restoring is in the web panel)")]
    [RequirePermission(BotPermissions.ManagePermissions)]
    public async Task BackupAsync()
    {
        // Personalities' avatars can make it a few megabytes.
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var backup = await Get<BackupMaker>().ExportAsync(Context.Guild!);
        await ModifyResponseAsync(m =>
        {
            m.Content = "The setup as it is now. Restore it (here or in another server) on the web panel's Backup page.";
            m.Attachments = [new AttachmentProperties(backup.FileName, new MemoryStream(Encoding.UTF8.GetBytes(backup.ToJson())))];
        });
    }
}
