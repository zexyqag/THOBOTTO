using Microsoft.EntityFrameworkCore;

using THOBOTTO.Access;
using THOBOTTO.Archive;
using THOBOTTO.Data;

namespace THOBOTTO.Panel;

// Saved attachment files, for people who may see the archive; inline, or as a download.
public static class ArchiveFiles
{
    public static async Task<IResult> ServeAsync(HttpContext context, ulong guildId, ulong id, bool download,
        PanelAccess access, IDbContextFactory<BotDbContext> dbFactory, IAttachmentStore store)
    {
        if (PanelAccess.UserId(context.User) is not { } userId || await access.InAsync(guildId, userId) is not { } found
            || !await access.CanAsync(found.Guild, found.Member, BotPermissions.ManageArchive))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        await using var db = await dbFactory.CreateDbContextAsync();
        var attachment = await db.ArchivedAttachments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id && db.ArchivedMessages.Any(m => m.Id == a.MessageId && m.GuildId == guildId));
        if (attachment?.StoredKey is not { } key || store.Open(key) is not { } file)
            return Results.NotFound();
        return Results.File(file, attachment.ContentType ?? "application/octet-stream", download ? attachment.FileName : null, enableRangeProcessing: true);
    }
}
