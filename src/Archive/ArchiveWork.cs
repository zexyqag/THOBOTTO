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
