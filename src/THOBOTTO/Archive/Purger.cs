using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;

namespace THOBOTTO.Archive;

public sealed record PurgeScope(ulong GuildId, ulong? MessageId, ulong? AuthorId, ulong? ChannelId)
{
    public string Describe() => MessageId is { } m ? $"message {m}"
        : AuthorId is { } a ? (ChannelId is { } c ? $"messages of <@{a}> in <#{c}>" : $"all messages of <@{a}>")
        : $"all messages in <#{ChannelId}>";
}

// Removes archived content for good: text, raw JSON, earlier versions, attachment records and
// files no other message still uses. A purged message keeps only its id, place, author and time.
// The audit log records who, when, why and how many, never what.
public sealed class Purger(IDbContextFactory<BotDbContext> dbFactory, IAttachmentStore store, TimeProvider time)
{
    public async Task<int> CountAsync(PurgeScope scope)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await Matching(db, scope).CountAsync();
    }

    public async Task<int> PurgeAsync(PurgeScope scope, ulong actorId, string reason)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();

        var ids = await Matching(db, scope).Select(m => m.Id).ToListAsync();
        var keys = await db.ArchivedAttachments
            .Where(a => ids.Contains(a.MessageId) && a.StoredKey != null)
            .Select(a => a.StoredKey!)
            .Distinct()
            .ToListAsync();

        await db.ArchivedAttachments.Where(a => ids.Contains(a.MessageId)).ExecuteDeleteAsync();
        await db.MessageVersions.Where(v => ids.Contains(v.MessageId)).ExecuteDeleteAsync();
        var now = time.GetUtcNow();
        await db.ArchivedMessages.Where(m => ids.Contains(m.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Content, (string?)null).SetProperty(m => m.Raw, (string?)null).SetProperty(m => m.PurgedAt, now));

        db.AuditEntries.Add(new()
        {
            GuildId = scope.GuildId,
            ActorId = actorId,
            Action = "archive.purge",
            Details = $"{scope.Describe()}: {ids.Count} message{(ids.Count == 1 ? "" : "s")}. Reason: {reason}",
            CreatedAt = now,
        });
        await db.SaveChangesAsync();

        // Files go only once nothing references them any more (they're shared by content).
        var stillUsed = await db.ArchivedAttachments.Where(a => a.StoredKey != null && keys.Contains(a.StoredKey)).Select(a => a.StoredKey!).ToListAsync();
        await transaction.CommitAsync();

        foreach (var key in keys.Except(stillUsed))
            await store.DeleteAsync(key, CancellationToken.None);

        return ids.Count;
    }

    private static IQueryable<ArchivedMessage> Matching(BotDbContext db, PurgeScope scope)
        => db.ArchivedMessages.Where(m => m.GuildId == scope.GuildId && m.PurgedAt == null
            && (scope.MessageId == null || m.Id == scope.MessageId)
            && (scope.AuthorId == null || m.AuthorId == scope.AuthorId)
            && (scope.ChannelId == null || m.ChannelId == scope.ChannelId));
}
