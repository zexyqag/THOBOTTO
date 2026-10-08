using System.Net;
using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.Archive;

// Gateway handlers queue message events; one writer applies them in order, so archiving never
// holds up the gateway. A second loop downloads attachments while their links are still valid.
public sealed class Archiver(
    IDbContextFactory<BotDbContext> dbFactory,
    IAttachmentStore store,
    ModuleState modules,
    SettingsStore settings,
    TimeProvider time,
    ILogger<Archiver> logger) : BackgroundService
{
    public const string ModuleId = "archive";

    private static readonly HttpClient Http = new();

    private abstract record Work(ulong GuildId, ulong ChannelId);
    private sealed record Saved(ulong GuildId, ulong ChannelId, RestMessage Message, bool Edit) : Work(GuildId, ChannelId);
    private sealed record Deleted(ulong GuildId, ulong ChannelId, IReadOnlyList<ulong> MessageIds) : Work(GuildId, ChannelId);

    private readonly System.Threading.Channels.Channel<Work> _queue = System.Threading.Channels.Channel.CreateUnbounded<Work>(new() { SingleReader = true });
    private readonly SemaphoreSlim _downloadWake = new(0, 1);

    public void OnCreated(ulong guildId, RestMessage message) => _queue.Writer.TryWrite(new Saved(guildId, message.ChannelId, message, Edit: false));

    public void OnUpdated(ulong guildId, RestMessage message) => _queue.Writer.TryWrite(new Saved(guildId, message.ChannelId, message, Edit: true));

    public void OnDeleted(ulong guildId, ulong channelId, IReadOnlyList<ulong> messageIds) => _queue.Writer.TryWrite(new Deleted(guildId, channelId, messageIds));

    // Backfill writes through here too, so its saves are ordered with live events.
    public void OnFetched(ulong guildId, RestMessage message) => OnCreated(guildId, message);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var downloads = DownloadLoopAsync(stoppingToken);

        await foreach (var work in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                if (!await modules.IsEnabledAsync(work.GuildId, ModuleId))
                    continue;
                var rules = await settings.GetAsync<ArchiveRules>(work.GuildId, ModuleId);
                if (rules.ExcludedChannelIds.Contains(work.ChannelId))
                    continue;

                await (work switch
                {
                    Saved saved => SaveAsync(saved, stoppingToken),
                    Deleted deleted => MarkDeletedAsync(deleted, stoppingToken),
                    _ => Task.CompletedTask,
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Archiving in channel {ChannelId} failed", work.ChannelId);
            }
        }

        await downloads;
    }

    private async Task SaveAsync(Saved work, CancellationToken ct)
    {
        var message = work.Message;
        var raw = RawJson.Of(message);
        var now = time.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var archived = await db.ArchivedMessages.FindAsync([message.Id], ct);
        if (archived is null)
        {
            db.ArchivedMessages.Add(new()
            {
                Id = message.Id,
                GuildId = work.GuildId,
                ChannelId = message.ChannelId,
                AuthorId = message.Author.Id,
                CreatedAt = message.CreatedAt,
                Content = message.Content,
                Raw = raw,
                EditedAt = message.EditedAt,
            });
        }
        else if (archived.PurgedAt is null && work.Edit)
        {
            // A real edit keeps the old version; other updates (link previews unfurling) just refresh it.
            if (archived.Content != message.Content)
                db.MessageVersions.Add(new() { MessageId = message.Id, Content = archived.Content, Raw = archived.Raw, ReplacedAt = message.EditedAt ?? now });
            archived.Content = message.Content;
            archived.Raw = raw;
            archived.EditedAt = message.EditedAt;
        }

        // Every fetch brings fresh attachment links; record new attachments and refresh old links.
        var known = await db.ArchivedAttachments.Where(a => a.MessageId == message.Id).ToDictionaryAsync(a => a.Id, ct);
        foreach (var attachment in message.Attachments)
        {
            if (known.TryGetValue(attachment.Id, out var existing))
            {
                existing.Url = attachment.Url;
                existing.UrlFetchedAt = now;
            }
            else
            {
                db.ArchivedAttachments.Add(new()
                {
                    Id = attachment.Id,
                    MessageId = message.Id,
                    FileName = attachment.FileName,
                    ContentType = attachment.ContentType,
                    Size = attachment.Size,
                    Url = attachment.Url,
                    UrlFetchedAt = now,
                });
            }
        }

        await db.SaveChangesAsync(ct);
        if (message.Attachments.Count > 0)
            WakeDownloads();
    }

    private async Task MarkDeletedAsync(Deleted work, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ArchivedMessages
            .Where(m => work.MessageIds.Contains(m.Id) && m.DeletedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedAt, now), ct);
    }

    private void WakeDownloads()
    {
        try
        {
            _downloadWake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    // Downloads attachments not stored yet, while their links are fresh (they expire after about a day).
    private async Task DownloadLoopAsync(CancellationToken ct)
    {
        if (!store.Enabled)
            return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Page by id: attachments the settings skip stay pending and mustn't block later ones.
                ulong after = 0;
                while (await DownloadBatchAsync(after, ct) is { } last)
                    after = last;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Downloading attachments failed");
            }

            await _downloadWake.WaitAsync(TimeSpan.FromMinutes(5), ct);
        }
    }

    // Returns the last attachment id looked at, or null when there was nothing left.
    private async Task<ulong?> DownloadBatchAsync(ulong after, CancellationToken ct)
    {
        var fresh = time.GetUtcNow() - TimeSpan.FromHours(20);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pending = await db.ArchivedAttachments
            .Where(a => a.Id > after && a.StoredKey == null && a.FailedAt == null && a.UrlFetchedAt > fresh)
            .Join(db.ArchivedMessages.Where(m => m.PurgedAt == null), a => a.MessageId, m => m.Id, (a, m) => new { Attachment = a, m.GuildId })
            .OrderBy(x => x.Attachment.Id)
            .Take(20)
            .ToListAsync(ct);

        foreach (var (attachment, guildId) in pending.Select(p => (p.Attachment, p.GuildId)))
        {
            var rules = await settings.GetAsync<ArchiveRules>(guildId, ModuleId);
            if (!rules.SaveAttachments || attachment.Size > rules.MaxAttachmentBytes)
                continue;

            try
            {
                var bytes = await Http.GetByteArrayAsync(attachment.Url, ct);
                attachment.StoredKey = await store.SaveAsync(bytes, ct);
                attachment.StoredAt = time.GetUtcNow();
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            {
                attachment.FailedAt = time.GetUtcNow();
            }
        }

        await db.SaveChangesAsync(ct);
        return pending.Count == 0 ? null : pending[^1].Attachment.Id;
    }
}
