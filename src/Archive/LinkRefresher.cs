using System.Net;

using Microsoft.EntityFrameworkCore;

using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.Archive;

// Attachments whose links went stale before they were stored get their message fetched again, at
// start and then daily. The fetch goes through the Archiver like a live one, which brings fresh
// links and queues the downloads. A message that is gone, or no longer carries the file, fails it.
public sealed class LinkRefresher(
    RestClient rest,
    Archiver archiver,
    IAttachmentStore store,
    IDbContextFactory<BotDbContext> dbFactory,
    ModuleState modules,
    SettingsStore settings,
    TimeProvider time,
    ILogger<LinkRefresher> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromDays(1);
    private const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!store.Enabled)
            return;

        using var timer = new PeriodicTimer(Every, time);
        do
        {
            try
            {
                // Page by id: attachments the settings skip keep waiting and mustn't block later ones.
                ulong after = 0;
                while (await RefreshBatchAsync(after, stoppingToken) is { } last)
                    after = last;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Refreshing stale attachment links failed; trying again tomorrow");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // Returns the last attachment id looked at, or null when there was nothing left.
    private async Task<ulong?> RefreshBatchAsync(ulong after, CancellationToken ct)
    {
        var stale = time.GetUtcNow() - Archiver.LinkLifetime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var waiting = await db.ArchivedAttachments
            .Where(a => a.Id > after && a.StoredKey == null && a.FailedAt == null && a.UrlFetchedAt <= stale)
            .Join(db.ArchivedMessages.Where(m => m.PurgedAt == null), a => a.MessageId, m => m.Id, (a, m) => new { Attachment = a, m.GuildId, m.ChannelId })
            .OrderBy(x => x.Attachment.Id)
            .Take(BatchSize)
            .ToListAsync(ct);

        foreach (var message in waiting.GroupBy(x => (x.GuildId, x.ChannelId, x.Attachment.MessageId)))
        {
            var (guildId, channelId, messageId) = message.Key;
            var attachments = message.Select(x => x.Attachment).ToList();
            if (!await WantedAsync(guildId, channelId, attachments))
                continue;

            try
            {
                var fetched = await rest.GetMessageAsync(channelId, messageId, cancellationToken: ct);
                archiver.OnFetched(guildId, fetched);
                foreach (var attachment in attachments.Where(a => !fetched.Attachments.Any(f => f.Id == a.Id)))
                    attachment.FailedAt = time.GetUtcNow();
            }
            catch (RestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            {
                foreach (var attachment in attachments)
                    attachment.FailedAt = time.GetUtcNow();
            }
            catch (RestException ex)
            {
                logger.LogWarning(ex, "Fetching message {MessageId} for fresh attachment links failed", messageId);
            }
        }

        await db.SaveChangesAsync(ct);
        return waiting.Count == 0 ? null : waiting[^1].Attachment.Id;
    }

    // Whether any of a message's attachments would be downloaded at all; otherwise a daily fetch would be for nothing.
    private async Task<bool> WantedAsync(ulong guildId, ulong channelId, IReadOnlyList<ArchivedAttachment> attachments)
    {
        if (!await modules.IsEnabledAsync(guildId, Archiver.ModuleId))
            return false;
        var rules = await settings.GetAsync<ArchiveRules>(guildId, Archiver.ModuleId);
        if (!rules.SaveAttachments || rules.ExcludedChannelIds.Contains(channelId))
            return false;
        return rules.MaxAttachmentBytes is not { } max || attachments.Any(a => a.Size <= max);
    }
}
