using Microsoft.EntityFrameworkCore;

using THOBOTTO.Access;
using THOBOTTO.Archive;
using THOBOTTO.Data;

namespace THOBOTTO.Panel;

// Saved attachment files, for people who may see the archive: inline when a browser can only show them, a download otherwise.
public static class ArchiveFiles
{
    // Whatever a browser could run as a page on the panel's origin (text/html, image/svg+xml) is kept off this list.
    private static readonly HashSet<string> InlineTypes = ["image/png", "image/jpeg", "image/gif", "image/webp", "video/mp4", "video/webm"];

    public static async Task<IResult> ServeAsync(HttpContext context, ulong guildId, ulong id, bool download,
        PanelAccess access, IDbContextFactory<BotDbContext> dbFactory, IAttachmentStore store)
    {
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (PanelAccess.UserId(context.User) is not { } userId || await access.InAsync(guildId, userId) is not { } found
            || !await access.CanAsync(found.Guild, found.Member, BotPermissions.ManageArchive))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        await using var db = await dbFactory.CreateDbContextAsync();
        var attachment = await db.ArchivedAttachments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id && db.ArchivedMessages.Any(m => m.Id == a.MessageId && m.GuildId == guildId));
        if (attachment?.StoredKey is not { } key || await store.OpenAsync(key, context.RequestAborted) is not { } file)
            return Results.NotFound();
        if (!download && InlineType(attachment.ContentType) is { } type)
        {
            context.Response.Headers.ContentSecurityPolicy = "sandbox";
            return Results.File(file, type, enableRangeProcessing: true);
        }
        return Results.File(file, "application/octet-stream", attachment.FileName, enableRangeProcessing: true);
    }

    public static bool Inline(string? contentType) => InlineType(contentType) is not null;

    private static string? InlineType(string? contentType)
    {
        if (contentType?.Split(';')[0].Trim().ToLowerInvariant() is not { Length: > 0 } type)
            return null;
        return InlineTypes.Contains(type) || type.StartsWith("audio/") ? type : null;
    }
}
