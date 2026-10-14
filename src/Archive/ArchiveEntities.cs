using NetCord;

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

    // From Discord's audit log: a moderator, or the author themselves. Null when it can't be told.
    public ulong? DeletedById { get; set; }

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

    // Set when the bot gave up on the file: it or its message is gone, or a few tries failed. A fresh link clears it.
    public DateTimeOffset? FailedAt { get; set; }
}

// A reaction on a message, current while RemovedAt is null. A removal keeps the row: history, not deletion.
public sealed class ArchivedReaction
{
    public long Id { get; init; }

    public ulong MessageId { get; init; }

    public ulong UserId { get; init; }

    // A custom emoji's id; null for a standard one, which EmojiName alone names.
    public ulong? EmojiId { get; init; }

    public required string EmojiName { get; init; }

    // A super reaction.
    public bool Burst { get; init; }

    public DateTimeOffset AddedAt { get; init; }

    public DateTimeOffset? RemovedAt { get; set; }
}

// A vote for a poll's answer, current while RemovedAt is null.
public sealed class ArchivedPollVote
{
    public long Id { get; init; }

    public ulong MessageId { get; init; }

    public ulong UserId { get; init; }

    public int AnswerId { get; init; }

    public DateTimeOffset AddedAt { get; init; }

    public DateTimeOffset? RemovedAt { get; set; }
}

// A channel, category or thread of the server as last seen, with Discord's own JSON of it.
// Never removed: one that is gone gets DeletedAt.
public sealed class ArchivedChannel
{
    public ulong Id { get; init; }

    public ulong GuildId { get; init; }

    // A channel's category, a thread's channel.
    public ulong? ParentId { get; set; }

    public ChannelType Type { get; set; }

    public required string Name { get; set; }

    public string? Topic { get; set; }

    public int? Position { get; set; }

    public bool Nsfw { get; set; }

    // Slowmode, in seconds.
    public int? RateLimit { get; set; }

    public ChannelFlags Flags { get; set; }

    // A forum's tags, as Discord's JSON.
    public string? AvailableTags { get; set; }

    // A forum post's tags, as a JSON array of tag ids.
    public string? AppliedTags { get; set; }

    // A thread's creator.
    public ulong? OwnerId { get; set; }

    public DateTimeOffset? ThreadCreatedAt { get; set; }

    public bool Archived { get; set; }

    public bool Locked { get; set; }

    // As Discord's JSON: id, type (0 role, 1 member), allow and deny.
    public string? PermissionOverwrites { get; set; }

    public required string Raw { get; set; }

    public DateTimeOffset FirstSeenAt { get; init; }

    public DateTimeOffset LastSeenAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class ArchivedRole
{
    public ulong Id { get; init; }

    public ulong GuildId { get; init; }

    public required string Name { get; set; }

    // As an RGB number, 0 for none.
    public int Color { get; set; }

    public int Position { get; set; }

    public ulong Permissions { get; set; }

    public bool Hoist { get; set; }

    public bool Mentionable { get; set; }

    public string? IconHash { get; set; }

    // Owned by an integration or a bot.
    public bool Managed { get; set; }

    public required string Raw { get; set; }

    public DateTimeOffset FirstSeenAt { get; init; }

    public DateTimeOffset LastSeenAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
}

// A member of the server as last seen; one who left gets LeftAt and stays.
public sealed class ArchivedMember
{
    public ulong GuildId { get; init; }

    public ulong UserId { get; init; }

    public required string Username { get; set; }

    public string? GlobalName { get; set; }

    public string? Nick { get; set; }

    public string? AvatarHash { get; set; }

    public string? GuildAvatarHash { get; set; }

    // A JSON array of role ids, as strings.
    public required string RoleIds { get; set; }

    public DateTimeOffset? JoinedAt { get; set; }

    // Boosting since.
    public DateTimeOffset? PremiumSince { get; set; }

    public bool Bot { get; set; }

    // Discord's member object, with its user inside.
    public required string Raw { get; set; }

    public DateTimeOffset FirstSeenAt { get; init; }

    public DateTimeOffset LastSeenAt { get; set; }

    public DateTimeOffset? LeftAt { get; set; }
}

// The kinds of GuildSnapshot. Channels, roles and members have tables of their own, so their rows
// carry only when they were last taken; the other kinds hold Discord's JSON.
public static class SnapshotKinds
{
    public const string Guild = "guild";
    public const string Channels = "channels";
    public const string Roles = "roles";
    public const string Members = "members";
    public const string Emojis = "emojis";
    public const string Stickers = "stickers";
    public const string Bans = "bans";
    public const string Webhooks = "webhooks";
    public const string Events = "events";
    // An object of channel id to its pins, each a message id and when it was pinned, in Discord's order.
    public const string Pins = "pins";
    // Not a snapshot: the last export of the server, as an ExportRecord.
    public const string Export = "export";
}

// The latest of one kind of snapshot of a server, and what stopped the last try.
public sealed class GuildSnapshot
{
    public ulong GuildId { get; init; }

    public required string Kind { get; init; }

    // When it was last taken whole; null when it never was.
    public DateTimeOffset? TakenAt { get; set; }

    public DateTimeOffset TriedAt { get; set; }

    public string? Json { get; set; }

    // Why the last try couldn't take it: a missing permission or intent, or Discord not answering.
    public string? Problem { get; set; }
}

public static class AssetKinds
{
    public const string Emoji = "emoji";
    public const string Sticker = "sticker";
    public const string Icon = "icon";
    public const string Banner = "banner";
    public const string Splash = "splash";
    public const string Avatar = "avatar";
    public const string RoleIcon = "role_icon";
}

// An image of the server's (emoji, sticker, icon, banner, splash, role icon) or a member's avatar, saved
// through the attachment store. The hash tells an image's versions apart; emojis and stickers have none.
public sealed class ArchivedAsset
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public required string Kind { get; init; }

    // The emoji, sticker, server, member or role the image belongs to.
    public ulong EntityId { get; init; }

    public required string Hash { get; init; }

    public required string Name { get; set; }

    public required string Url { get; set; }

    public string? ContentType { get; set; }

    public string? StoredKey { get; set; }

    public DateTimeOffset? FetchedAt { get; set; }

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

    // The server's name when the backfill last started, for an export when Discord can't be asked.
    public string? Name { get; set; }

    public bool Running { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
}
