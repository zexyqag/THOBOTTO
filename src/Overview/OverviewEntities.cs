namespace THOBOTTO.Overview;

// A widget placed in a channel: one message the bot keeps up to date, in order with the channel's other widgets.
public sealed class OverviewWidget
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public ulong ChannelId { get; set; }

    public required string Kind { get; init; }

    public int Order { get; set; }

    // The kind's options as JSON: { "count": "5" }.
    public string Options { get; set; } = "{}";

    public ulong? MessageId { get; set; }

    // What the message shows now, to skip edits that change nothing.
    public string? Shown { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}
