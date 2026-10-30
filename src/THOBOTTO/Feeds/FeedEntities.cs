namespace THOBOTTO.Feeds;

// A feed a server follows, and where its new entries are posted.
public sealed class NewsFeed
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    // The feed itself, after a page's own feed was found or a shortcut was followed.
    public required string Url { get; init; }

    // The feed's name when it was followed (a Steam game's, where the feed has only a number).
    public required string Title { get; init; }

    // A text or announcement channel, or a forum (each entry a post of its own).
    public ulong ChannelId { get; init; }

    public ulong AddedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    // The keys of the entries seen lately (JSON), so nothing is posted twice and the backlog never is.
    public string Seen { get; set; } = "[]";

    public string? ETag { get; set; }

    public string? LastModified { get; set; }

    public DateTimeOffset? CheckedAt { get; set; }

    public DateTimeOffset? PostedAt { get; set; }

    // Why the last checks failed, and how many in a row.
    public string? Problem { get; set; }

    public int Failures { get; set; }
}
