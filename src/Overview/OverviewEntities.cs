namespace THOBOTTO.Overview;

// A widget placed in a channel: one message the bot keeps up to date, in order with the channel's other widgets.
public sealed class OverviewWidget
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public ulong ChannelId { get; set; }

    public int Order { get; set; }

    public string? Title { get; set; }

    // 0xRRGGBB; null is the default.
    public int? Colour { get; set; }

    public string Style { get; set; } = WidgetStyles.CardsLines;

    // Rows of cells as JSON (WidgetLayout).
    public string Layout { get; set; } = "[]";

    public ulong? MessageId { get; set; }

    // What the message shows now, to skip edits that change nothing.
    public string? Shown { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}
