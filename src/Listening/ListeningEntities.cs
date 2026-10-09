namespace THOBOTTO.Listening;

// A member who wants the bot to listen to them in this server's voice channels (voice commands).
// Nobody else's voice is ever decoded.
public sealed class ListeningOptIn
{
    public ulong GuildId { get; init; }

    public ulong UserId { get; init; }

    public DateTimeOffset At { get; init; }
}
