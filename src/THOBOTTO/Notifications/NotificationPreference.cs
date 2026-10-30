namespace THOBOTTO.Notifications;

// A row means the member wants DMs for that topic in that guild.
public sealed class NotificationPreference
{
    public ulong GuildId { get; init; }

    public ulong UserId { get; init; }

    public required string Topic { get; init; }
}
