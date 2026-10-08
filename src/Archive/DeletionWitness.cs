using NetCord;
using NetCord.Rest;

namespace THOBOTTO.Archive;

public sealed record DeleteEntry(ulong Id, ulong? UserId, ulong? TargetId, ulong? ChannelId, int Count);

// Reads who deleted a message from Discord's audit log. Discord logs only deletions of someone
// else's message, and folds repeats (same deleter, author and channel, within a while) into one
// entry whose count goes up. So the counts seen are remembered: a new entry or a higher count is
// the deletion just made, and no entry at all means the authors deleted it themselves.
public sealed class DeletionWitness(RestClient rest, TimeProvider time, ILogger<DeletionWitness> logger)
{
    // The audit entry can land a moment after the gateway's delete event.
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(ulong, AuditLogEvent), Dictionary<ulong, int>> _seen = [];

    // Who deleted an author's message in a channel; null when it can't be told.
    public Task<ulong?> WhoDeletedAsync(ulong guildId, ulong channelId, ulong authorId)
        => LookAsync(guildId, AuditLogEvent.MessageDelete, e => e.TargetId == authorId && e.ChannelId == channelId, selfDelete: authorId);

    // Who deleted many messages at once in a channel (a purge).
    public Task<ulong?> WhoBulkDeletedAsync(ulong guildId, ulong channelId)
        => LookAsync(guildId, AuditLogEvent.MessageBulkDelete, e => e.TargetId == channelId, selfDelete: null);

    // Notes the counts as they are, so the first deletion after a start can be told too.
    public async Task PrimeAsync(ulong guildId)
    {
        await LookAsync(guildId, AuditLogEvent.MessageDelete, _ => false, null, settle: false);
        await LookAsync(guildId, AuditLogEvent.MessageBulkDelete, _ => false, null, settle: false);
    }

    private async Task<ulong?> LookAsync(ulong guildId, AuditLogEvent action, Func<DeleteEntry, bool> fits, ulong? selfDelete, bool settle = true)
    {
        if (settle)
            await Task.Delay(Settle, time);
        await _gate.WaitAsync();
        try
        {
            List<DeleteEntry> entries;
            try
            {
                entries = await rest.GetGuildAuditLogAsync(guildId, new() { ActionType = action, BatchSize = 25 })
                    .Take(25)
                    .Select(e => new DeleteEntry(e.Id, e.UserId, e.TargetId, e.Options?.ChannelId, e.Options?.Count ?? 1))
                    .ToListAsync();
            }
            catch (RestException ex)
            {
                // Without View Audit Log it can't be told.
                logger.LogDebug("Reading the audit log of {GuildId} failed: {Message}", guildId, ex.Message);
                return null;
            }

            var key = (guildId, action);
            var seen = _seen.GetValueOrDefault(key);
            var who = DeletionClues.Match(entries, seen, fits, time.GetUtcNow(), selfDelete);
            _seen[key] = entries.ToDictionary(e => e.Id, e => e.Count);
            return who;
        }
        finally
        {
            _gate.Release();
        }
    }
}

public static class DeletionClues
{
    // How recent a never-seen entry must be to count as this deletion.
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);

    // Entries newest first. Without earlier counts (just started), a folded repeat can't be told
    // from a self-deletion, so only a fresh entry is trusted then.
    public static ulong? Match(IReadOnlyList<DeleteEntry> entries, IReadOnlyDictionary<ulong, int>? seen, Func<DeleteEntry, bool> fits, DateTimeOffset now, ulong? selfDelete)
    {
        foreach (var entry in entries.Where(fits))
        {
            var isNew = seen is not null && seen.TryGetValue(entry.Id, out var count)
                ? entry.Count > count
                : CreatedAt(entry.Id) >= now - Fresh;
            if (isNew)
                return entry.UserId;
        }
        return seen is null ? null : selfDelete;
    }

    // Discord ids carry their creation time: milliseconds since 2015 in the top bits.
    public static DateTimeOffset CreatedAt(ulong id) => DateTimeOffset.FromUnixTimeMilliseconds((long)(id >> 22) + 1_420_070_400_000);
}
