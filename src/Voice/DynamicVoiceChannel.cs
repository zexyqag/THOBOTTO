namespace THOBOTTO.Voice;

public sealed class DynamicVoiceChannel
{
    public ulong ChannelId { get; init; }

    public ulong GuildId { get; init; }

    public ulong OwnerId { get; init; }

    // The name it was made with ("Ana's channel"), to go back to.
    public required string Name { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
