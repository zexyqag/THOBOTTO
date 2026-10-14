using NetCord;
using NetCord.Rest;

namespace THOBOTTO.Archive;

// What the Archiver's writer applies, in the order it was queued. Each item names its server and, for the
// excluded-channels rule, its channel (0 where there is none).
internal abstract record Work(ulong GuildId, ulong ChannelId);

// Where a saved message came from: the gateway's create or update, or a REST fetch (backfill, catch-up, a refresh).
internal enum SaveSource
{
    Created,
    Updated,
    Fetched,
}

internal sealed record Saved(ulong GuildId, ulong ChannelId, RestMessage Message, SaveSource Source) : Work(GuildId, ChannelId);

internal sealed record Deleted(ulong GuildId, ulong ChannelId, IReadOnlyList<ulong> MessageIds) : Work(GuildId, ChannelId);

internal sealed record ReactionAdded(ulong GuildId, ulong ChannelId, ulong MessageId, SeenReaction Reaction) : Work(GuildId, ChannelId);

internal sealed record ReactionRemoved(ulong GuildId, ulong ChannelId, ulong MessageId, SeenReaction Reaction) : Work(GuildId, ChannelId);

internal sealed record ReactionsRemovedAll(ulong GuildId, ulong ChannelId, ulong MessageId) : Work(GuildId, ChannelId);

internal sealed record ReactionsRemovedEmoji(ulong GuildId, ulong ChannelId, ulong MessageId, ulong? EmojiId, string EmojiName) : Work(GuildId, ChannelId);

// Everyone reacting now, as Discord lists them; the rows are brought in line.
internal sealed record ReactionsFetched(ulong GuildId, ulong ChannelId, ulong MessageId, IReadOnlyList<SeenReaction> Reactions) : Work(GuildId, ChannelId);

internal sealed record VoteAdded(ulong GuildId, ulong ChannelId, ulong MessageId, SeenVote Vote) : Work(GuildId, ChannelId);

internal sealed record VoteRemoved(ulong GuildId, ulong ChannelId, ulong MessageId, SeenVote Vote) : Work(GuildId, ChannelId);

internal sealed record VotesFetched(ulong GuildId, ulong ChannelId, ulong MessageId, IReadOnlyList<SeenVote> Votes) : Work(GuildId, ChannelId);

// A message whose fetched counts don't add up to its rows: Discord is asked who reacted (per emoji listed) and
// who voted (per answer listed).
internal sealed record ReactionCheck(ulong GuildId, ulong ChannelId, ulong MessageId, IReadOnlyList<ReactionCount> Reactions, IReadOnlyList<int> AnswerIds);

internal sealed record ChannelsListed(ulong GuildId, GuildChannelList List) : Work(GuildId, 0);

internal sealed record ChannelSeen(ulong GuildId, IGuildChannel Channel) : Work(GuildId, 0);

// The channel as it was when deleted, when the event carried it.
internal sealed record ChannelGone(ulong GuildId, ulong Id, IGuildChannel? Last) : Work(GuildId, 0);

internal sealed record RolesListed(ulong GuildId, IReadOnlyList<Role> Roles) : Work(GuildId, 0);

internal sealed record RoleSeen(ulong GuildId, Role Role) : Work(GuildId, 0);

internal sealed record RoleGone(ulong GuildId, ulong Id) : Work(GuildId, 0);

internal sealed record MembersListed(ulong GuildId, IReadOnlyList<GuildUser> Members) : Work(GuildId, 0);

internal sealed record MemberSeen(ulong GuildId, GuildUser Member) : Work(GuildId, 0);

internal sealed record MemberLeft(ulong GuildId, User User) : Work(GuildId, 0);

// A snapshot of one kind taken (Json for the kinds that keep one), or what stopped it.
internal sealed record SnapshotTaken(ulong GuildId, string Kind, string? Json, string? Problem) : Work(GuildId, 0);

// The pins of some channels (Complete: of every channel that has any).
internal sealed record PinsSeen(ulong GuildId, IReadOnlyDictionary<ulong, IReadOnlyList<SeenPin>> Pins, bool Complete) : Work(GuildId, 0);
