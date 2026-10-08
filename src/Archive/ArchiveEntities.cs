namespace THOBOTTO.Archive;

// A message as it is now (or was when deleted). Earlier versions are MessageVersions.
public sealed class ArchivedMessage
{
    public ulong Id { get; init; }

    public ulong GuildId { get; init; }

    // The channel or thread it was posted in.
    public ulong ChannelId { get; init; }

    public ulong AuthorId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public string? Content { get; set; }

    // Discord's own JSON for the message, so nothing (embeds, polls, stickers…) is lost for an export.
    public string? Raw { get; set; }

    public DateTimeOffset? EditedAt { get; set; }

    // Kept when deleted, only marked.
    public DateTimeOffset? DeletedAt { get; set; }

    // Purged messages lose their content for real; who and why is in the audit log.
    public DateTimeOffset? PurgedAt { get; set; }
}

// A version the message had before an edit.
public sealed class MessageVersion
{
    public long Id { get; init; }

    public ulong MessageId { get; init; }

    public string? Content { get; set; }

    public string? Raw { get; set; }

    // When this version stopped being current.
    public DateTimeOffset ReplacedAt { get; init; }
}

public sealed class ArchivedAttachment
{
    public ulong Id { get; init; }

    public ulong MessageId { get; init; }

    public required string FileName { get; init; }

    public string? ContentType { get; init; }

    public long Size { get; init; }

    // Signed and expiring: only good for a day or so after the message was fetched.
    public required string Url { get; set; }

    public DateTimeOffset UrlFetchedAt { get; set; }

    // Where the file is kept, if it was saved.
    public string? StoredKey { get; set; }

    public DateTimeOffset? StoredAt { get; set; }

    // Set when downloading failed for good (gone, or the link expired).
    public DateTimeOffset? FailedAt { get; set; }
}

// Backfill progress for one channel or thread.
public sealed class BackfillChannel
{
    public ulong ChannelId { get; init; }

    public ulong GuildId { get; init; }

    public required string Name { get; set; }

    // The oldest message fetched so far; the next page starts before it. Null: not started.
    public ulong? Cursor { get; set; }

    public int Fetched { get; set; }

    public bool Done { get; set; }

    // Why it was skipped, e.g. no access.
    public string? Problem { get; set; }
}

// Whether a guild's backfill is running.
public sealed class BackfillRun
{
    public ulong GuildId { get; init; }

    public bool Running { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
}
