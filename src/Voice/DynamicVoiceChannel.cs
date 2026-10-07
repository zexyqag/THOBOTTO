namespace THOBOTTO.Voice;

public sealed class DynamicVoiceChannel
{
    public ulong ChannelId { get; init; }

    public ulong GuildId { get; init; }

    public ulong OwnerId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
