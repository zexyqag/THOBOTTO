using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.JsonModels;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Modules;

// The queue, not NetCord's channel.
using Channel = System.Threading.Channels.Channel;

namespace THOBOTTO.Archive;

// Gateway handlers, the backfill and the snapshotter queue what they see; one writer applies it in order, so
// archiving never holds up the gateway and nothing races over a row. A second loop downloads attachments
// while their links are still valid.
public sealed class Archiver(
    IDbContextFactory<BotDbContext> dbFactory,
    IAttachmentStore store,
    DeletionWitness witness,
    ReactionLedger reactions,
    ServerLedger server,
    ModuleState modules,
    SettingsStore settings,
    [FromKeyedServices(nameof(Archiver))] HttpClient http,
    TimeProvider time,
    ILogger<Archiver> logger) : BackgroundService
{
    public const string ModuleId = "archive";

    // Attachment links are signed and last about a day; past this they count as stale.
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(20);

    internal const int DownloadBatch = 20;

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(3);
    private const int DownloadTries = 3;

    private readonly Channel<Work> _queue = Channel.CreateUnbounded<Work>(new() { SingleReader = true });
    private readonly Channel<ReactionCheck> _checks = Channel.CreateUnbounded<ReactionCheck>(new() { SingleReader = true });
    private readonly SemaphoreSlim _downloadWake = new(0, 1);
    private readonly DownloadAttempts _attempts = new(DownloadTries);
    private readonly RecentlyGone _deleted = new(TimeSpan.FromHours(1));

    public void OnCreated(ulong guildId, RestMessage message) => Enqueue(new Saved(guildId, message.ChannelId, message, SaveSource.Created, null));

    public void OnUpdated(ulong guildId, RestMessage message) => Enqueue(new Saved(guildId, message.ChannelId, message, SaveSource.Updated, null));

    public void OnDeleted(ulong guildId, ulong channelId, IReadOnlyList<ulong> messageIds) => Enqueue(new Deleted(guildId, channelId, messageIds));

    // Backfill, catch-up and refreshes write through here too, so their saves are ordered with live events;
    // askedAt is from before the request, so the answer is never taken for newer than it is.
    public void OnFetched(ulong guildId, RestMessage message, DateTimeOffset askedAt) => Enqueue(new Saved(guildId, message.ChannelId, message, SaveSource.Fetched, askedAt));

    // The ids a fetched page held, after its messages, and the id range the page vouches for (ArchiveSync.Covered):
    // what's known in the range and wasn't returned is gone.
    public void OnPageFetched(ulong guildId, ulong channelId, IReadOnlyList<ulong> messageIds, ulong from, ulong to)
        => Enqueue(new PageFetched(guildId, channelId, messageIds, from, to));

    public void OnReactionAdded(ulong guildId, ulong channelId, ulong messageId, SeenReaction reaction) => Enqueue(new ReactionAdded(guildId, channelId, messageId, reaction));

    public void OnReactionRemoved(ulong guildId, ulong channelId, ulong messageId, SeenReaction reaction) => Enqueue(new ReactionRemoved(guildId, channelId, messageId, reaction));

    public void OnReactionsRemovedAll(ulong guildId, ulong channelId, ulong messageId) => Enqueue(new ReactionsRemovedAll(guildId, channelId, messageId));

    public void OnReactionsRemovedEmoji(ulong guildId, ulong channelId, ulong messageId, ulong? emojiId, string emojiName)
        => Enqueue(new ReactionsRemovedEmoji(guildId, channelId, messageId, emojiId, emojiName));

    public void OnReactionsFetched(ulong guildId, ulong channelId, ulong messageId, IReadOnlyList<SeenReaction> reactions, DateTimeOffset askedAt)
        => Enqueue(new ReactionsFetched(guildId, channelId, messageId, reactions, askedAt));

    public void OnVoteAdded(ulong guildId, ulong channelId, ulong messageId, SeenVote vote) => Enqueue(new VoteAdded(guildId, channelId, messageId, vote));

    public void OnVoteRemoved(ulong guildId, ulong channelId, ulong messageId, SeenVote vote) => Enqueue(new VoteRemoved(guildId, channelId, messageId, vote));

    public void OnVotesFetched(ulong guildId, ulong channelId, ulong messageId, IReadOnlyList<SeenVote> votes, DateTimeOffset askedAt)
        => Enqueue(new VotesFetched(guildId, channelId, messageId, votes, askedAt));

    // The server's structure: a complete listing (begun at startedAt) marks what's gone, a sighting adds or refreshes one.
    public void OnChannelsListed(ulong guildId, GuildChannelList list, DateTimeOffset startedAt) => Enqueue(new ChannelsListed(guildId, list, startedAt));

    public void OnChannelSeen(ulong guildId, IGuildChannel channel) => Enqueue(new ChannelSeen(guildId, channel));

    public void OnChannelGone(ulong guildId, ulong channelId, IGuildChannel? last) => Enqueue(new ChannelGone(guildId, channelId, last));

    public void OnRolesListed(ulong guildId, IReadOnlyList<Role> roles, DateTimeOffset startedAt) => Enqueue(new RolesListed(guildId, roles, startedAt));

    public void OnRoleSeen(ulong guildId, Role role) => Enqueue(new RoleSeen(guildId, role));

    public void OnRoleGone(ulong guildId, ulong roleId) => Enqueue(new RoleGone(guildId, roleId));

    public void OnMembersListed(ulong guildId, IReadOnlyList<GuildUser> members, DateTimeOffset startedAt) => Enqueue(new MembersListed(guildId, members, startedAt));

    public void OnMemberSeen(ulong guildId, GuildUser member) => Enqueue(new MemberSeen(guildId, member));

    public void OnMemberLeft(ulong guildId, User user) => Enqueue(new MemberLeft(guildId, user));

    public void OnSnapshotTaken(ulong guildId, string kind, string? json, string? problem) => Enqueue(new SnapshotTaken(guildId, kind, json, problem));

    public void OnPinsSeen(ulong guildId, IReadOnlyDictionary<ulong, IReadOnlyList<SeenPin>> pins, bool complete) => Enqueue(new PinsSeen(guildId, pins, complete));

    // Events waiting to be written; the backfill holds back while this is high. Counted here:
    // a single-reader channel can't count itself.
    public int Pending => Volatile.Read(ref _pending);

    // Messages waiting for Discord to say who reacted; the backfill holds back on this too.
    public int ChecksPending => Volatile.Read(ref _checksPending);

    private int _pending;
    private int _checksPending;

    private void Enqueue(Work work)
    {
        Interlocked.Increment(ref _pending);
        _queue.Writer.TryWrite(work);
    }

    // The messages to ask Discord about, for the ReactionReconciler.
    internal async IAsyncEnumerable<ReactionCheck> ChecksAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var check in _checks.Reader.ReadAllAsync(ct))
        {
            Interlocked.Decrement(ref _checksPending);
            yield return check;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var downloads = DownloadLoopAsync(stoppingToken);

        await foreach (var work in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            Interlocked.Decrement(ref _pending);
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
                    PageFetched page => ReconcilePageAsync(page, stoppingToken),
                    ReactionAdded or ReactionRemoved or ReactionsRemovedAll or ReactionsRemovedEmoji or ReactionsFetched or VoteAdded or VoteRemoved or VotesFetched
                        => reactions.ApplyAsync(work, stoppingToken),
                    _ => server.ApplyAsync(work, stoppingToken),
                });
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Archiving in channel {ChannelId} of {GuildId} failed", work.ChannelId, work.GuildId);
            }
        }

        await downloads;
    }

    private async Task SaveAsync(Saved work, CancellationToken ct)
    {
        var message = work.Message;
        var json = ((IJsonModel<JsonMessage>)message).JsonModel;
        var raw = RawJson.Of(json);
        var now = time.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var archived = await db.ArchivedMessages.FindAsync([message.Id], ct);
        if (archived?.PurgedAt is not null)
            return; // Purged stays purged, even if the message still exists on Discord.
        var known = archived is not null;
        if (archived is null)
        {
            // A partial update of a message never seen says too little to make a row of.
            if (json.Author is null)
                return;
            var row = new ArchivedMessage
            {
                Id = message.Id,
                GuildId = work.GuildId,
                ChannelId = message.ChannelId,
                AuthorId = json.Author.Id,
                CreatedAt = message.CreatedAt,
                Content = json.Content,
                Raw = raw,
                EditedAt = json.EditedAt,
            };
            // The gateway said it was deleted while there was no row to mark: the fetch that brings it was asked before.
            if (_deleted.TryGet(message.Id, out var goneAt))
                row.DeletedAt = goneAt;
            db.ArchivedMessages.Add(row);
        }
        else if (work.Source == SaveSource.Created)
        {
            // A fetch made the row first; the create lends it the members only the gateway sends.
            if (archived.Raw is { } stored)
                archived.Raw = RawJson.WithMembers(stored, raw);
        }
        else
        {
            switch (ArchiveSync.Decide(archived.Content, archived.EditedAt, json.Content, json.EditedAt, json.Author is not null))
            {
                case SaveStep.Stale:
                    break;
                case SaveStep.Partial:
                    archived.Raw = RawJson.Merge(archived.Raw, raw);
                    archived.EditedAt = json.EditedAt ?? archived.EditedAt;
                    break;
                case SaveStep.Edited:
                    db.MessageVersions.Add(new() { MessageId = message.Id, Content = archived.Content, Raw = archived.Raw, ReplacedAt = json.EditedAt ?? now });
                    archived.Content = json.Content;
                    archived.Raw = RawJson.WithMembers(raw, archived.Raw);
                    archived.EditedAt = json.EditedAt;
                    break;
                default:
                    // Link previews unfurling, reactions, a pin: the JSON as Discord has it now.
                    archived.Raw = RawJson.WithMembers(raw, archived.Raw);
                    break;
            }
            // Discord still returned it after the mark was made, so a deletion marked without an event was wrong.
            if (archived.DeletedAt is { } deleted && deleted < work.AskedAt)
            {
                archived.DeletedAt = null;
                archived.DeletedById = null;
            }
        }

        // Every fetch brings fresh attachment links; record new attachments, refresh old links and give failed downloads another go.
        var attachments = await db.ArchivedAttachments.Where(a => a.MessageId == message.Id).ToDictionaryAsync(a => a.Id, ct);
        foreach (var attachment in message.Attachments)
        {
            if (attachments.TryGetValue(attachment.Id, out var existing))
            {
                existing.Url = attachment.Url;
                existing.UrlFetchedAt = now;
                existing.FailedAt = null;
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
        if (work.AskedAt is { } askedAt)
            await CheckCountsAsync(db, work, known, askedAt, ct);
    }

    // A fetched message carries its reaction and poll counts; when the rows don't add up to them, Discord is
    // asked who, unless there is nothing to ask about and the rows just go. A message seen for the first time
    // has no rows to compare.
    private async Task CheckCountsAsync(BotDbContext db, Saved work, bool known, DateTimeOffset askedAt, CancellationToken ct)
    {
        var message = work.Message;
        var counts = message.Reactions
            .Select(r => r.CountDetails is { } d ? new ReactionCount(r.Emoji.Id, r.Emoji.Name ?? "", d.Normal, d.Burst) : new(r.Emoji.Id, r.Emoji.Name ?? "", r.Count, 0))
            .Where(c => c.Normal > 0 || c.Burst > 0)
            .ToList();
        // A poll without results is one Discord didn't count for this answer, not one without votes.
        var results = message.Poll?.Results?.Answers.Select(a => (a.AnswerId, a.Count)).Where(a => a.Count > 0).ToList();
        if (!known && counts.Count == 0 && results is not { Count: > 0 })
            return;

        var askReactions = false;
        if (counts.Count > 0 || known)
        {
            var current = await db.ArchivedReactions.Where(r => r.MessageId == message.Id && r.RemovedAt == null).ToListAsync(ct);
            if (!ReactionChanges.CountsMatch(current, counts))
            {
                if (counts.Count == 0)
                    OnReactionsFetched(work.GuildId, work.ChannelId, message.Id, [], askedAt);
                else
                    askReactions = true;
            }
        }

        List<int> answers = [];
        if (results is not null && (results.Count > 0 || known))
        {
            var current = await db.ArchivedPollVotes.Where(v => v.MessageId == message.Id && v.RemovedAt == null).ToListAsync(ct);
            if (!VoteChanges.CountsMatch(current, results))
            {
                if (results.Count == 0)
                    OnVotesFetched(work.GuildId, work.ChannelId, message.Id, [], askedAt);
                else
                    answers = results.Select(a => a.AnswerId).ToList();
            }
        }

        if (askReactions || answers.Count > 0)
        {
            Interlocked.Increment(ref _checksPending);
            _checks.Writer.TryWrite(new(work.GuildId, work.ChannelId, message.Id, askReactions ? counts : [], answers, askedAt));
        }
    }

    // A fetched page says what Discord still has in the range it covers: a known message in it that it didn't
    // return was deleted while the bot wasn't looking, by nobody it can name.
    private async Task ReconcilePageAsync(PageFetched work, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var known = await db.ArchivedMessages
            .Where(m => m.ChannelId == work.ChannelId && m.Id >= work.From && m.Id <= work.To && m.DeletedAt == null)
            .Select(m => m.Id)
            .ToListAsync(ct);
        var missing = known.Except(work.MessageIds).Order().ToList();
        if (missing.Count == 0)
            return;

        var now = time.GetUtcNow();
        await db.ArchivedMessages.Where(m => missing.Contains(m.Id)).ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedAt, now), ct);
        logger.LogInformation("{Count} messages in channel {ChannelId} of {GuildId} are gone from Discord; marked deleted", missing.Count, work.ChannelId, work.GuildId);
    }

    // The gateway's word on a deletion: its time stands, also over a page's earlier guess, and it may be named.
    private async Task MarkDeletedAsync(Deleted work, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ArchivedMessages
            .Where(m => work.MessageIds.Contains(m.Id))
            .Select(m => new { m.Id, m.AuthorId, Done = m.DeletedAt != null && m.DeletedById != null })
            .ToListAsync(ct);
        // No row yet: a fetch asked before the deletion may still bring the message.
        foreach (var id in work.MessageIds.Where(id => !rows.Any(r => r.Id == id)))
            _deleted.Note(id, now);
        var marked = rows.Where(r => !r.Done).ToList();
        if (marked.Count == 0)
            return;
        var ids = marked.Select(m => m.Id).ToList();
        await db.ArchivedMessages.Where(m => ids.Contains(m.Id)).ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedAt, now), ct);

        // Asking the audit log waits a moment for Discord to write it; the queue goes on meanwhile.
        _ = AttributeAsync(work, marked.Count == 1 && work.MessageIds.Count == 1 ? marked[0].AuthorId : null, ids);
    }

    private async Task AttributeAsync(Deleted work, ulong? authorId, IReadOnlyList<ulong> ids)
    {
        try
        {
            var who = witness.Expected(ids) ?? (authorId is { } author
                ? await witness.WhoDeletedAsync(work.GuildId, work.ChannelId, author)
                : await witness.WhoBulkDeletedAsync(work.GuildId, work.ChannelId));
            if (who is null)
                return;
            await using var db = await dbFactory.CreateDbContextAsync();
            await db.ArchivedMessages.Where(m => ids.Contains(m.Id)).ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedById, who));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Finding who deleted messages in {ChannelId} of {GuildId} failed", work.ChannelId, work.GuildId);
        }
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

    // Downloads attachments not stored yet, while their links are fresh; the LinkRefresher brings
    // fresh ones for the rest. Nothing awaits this while the bot runs, so only a shutdown may end it.
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
                await _downloadWake.WaitAsync(TimeSpan.FromMinutes(5), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Downloading attachments failed; trying again in a minute");
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
        }
    }

    // Returns the last attachment id looked at, or null when there was nothing left.
    private async Task<ulong?> DownloadBatchAsync(ulong after, CancellationToken ct)
    {
        var fresh = time.GetUtcNow() - LinkLifetime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pending = await db.ArchivedAttachments
            .Where(a => a.Id > after && a.StoredKey == null && a.FailedAt == null && a.UrlFetchedAt > fresh)
            .Join(db.ArchivedMessages.Where(m => m.PurgedAt == null), a => a.MessageId, m => m.Id, (a, m) => new { Attachment = a, m.GuildId })
            .OrderBy(x => x.Attachment.Id)
            .Take(DownloadBatch)
            .ToListAsync(ct);

        foreach (var (attachment, guildId) in pending.Select(p => (p.Attachment, p.GuildId)))
        {
            var rules = await settings.GetAsync<ArchiveRules>(guildId, ModuleId);
            if (!rules.SaveAttachments || attachment.Size > rules.MaxAttachmentBytes)
                continue;

            await StoreAsync(attachment, guildId, ct);
            await db.SaveChangesAsync(ct);
        }

        return pending.Count == 0 ? null : pending[^1].Attachment.Id;
    }

    // Downloads and keeps the file, or marks a failed try: a gone file fails for good, anything else after a few tries.
    private async Task StoreAsync(ArchivedAttachment attachment, ulong guildId, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(DownloadTimeout);
            var bytes = await http.GetByteArrayAsync(attachment.Url, timeout.Token);
            attachment.StoredKey = await store.SaveAsync(bytes, ct);
            attachment.StoredAt = time.GetUtcNow();
            _attempts.Succeeded(attachment.Id);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            attachment.FailedAt = time.GetUtcNow();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (_attempts.Failed(attachment.Id))
            {
                attachment.FailedAt = time.GetUtcNow();
                logger.LogWarning(ex, "Storing attachment {AttachmentId} of {GuildId} failed {Tries} times; giving up", attachment.Id, guildId, DownloadTries);
            }
            else
                logger.LogWarning(ex, "Storing attachment {AttachmentId} of {GuildId} failed; trying again later", attachment.Id, guildId);
        }
    }
}

// Counts an attachment's failed downloads, so the bot gives up after a few.
public sealed class DownloadAttempts(int tries)
{
    private readonly Dictionary<ulong, int> _failed = new();

    // Records a failed try; true when it was the last one allowed.
    public bool Failed(ulong attachmentId)
    {
        var failed = _failed.GetValueOrDefault(attachmentId) + 1;
        if (failed < tries)
        {
            _failed[attachmentId] = failed;
            return false;
        }
        _failed.Remove(attachmentId);
        return true;
    }

    public void Succeeded(ulong attachmentId) => _failed.Remove(attachmentId);
}
