namespace THOBOTTO.Notifications;

public sealed record NotificationTopic(string Id, string Description);

// What members can ask to be DMed about, with /me notifications. Modules with per-item topics (one per game)
// add theirs through INotificationTopicSource.
public static class NotificationTopics
{
    public const string MischiefYou = "mischief.you";
    public const string BetResults = "bets.result";
    public const string EmojiDecisions = "emojis.decision";
    public const string FameYou = "fame.you";
    public const string EventsNew = "events.new";
    public const string EventsReminder = "events.reminder";
    public const string WrappedYou = "wrapped.you";
    public const string QuotedYou = "quotes.you";

    public static IReadOnlyList<NotificationTopic> Fixed { get; } =
    [
        new(MischiefYou, "Someone renamed, painted or locked you"),
        new(BetResults, "A bet you staked on was resolved or cancelled"),
        new(EmojiDecisions, "Your emoji or sticker proposal was accepted or rejected"),
        new(FameYou, "Your message made the hall of fame"),
        new(EventsNew, "Someone planned a new event"),
        new(EventsReminder, "Reminders and changes for events you're in"),
        new(WrappedYou, "Your own Wrapped, when the server's year in review comes out"),
        new(QuotedYou, "Someone saved a quote of what you said in voice"),
    ];
}

public interface INotificationTopicSource
{
    Task<IReadOnlyList<NotificationTopic>> TopicsAsync(ulong guildId);
}
