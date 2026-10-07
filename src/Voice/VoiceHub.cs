namespace THOBOTTO.Voice;

// Joining a hub channel gives the member a new voice channel of their own.
public sealed class VoiceHub
{
    public ulong ChannelId { get; init; }

    public ulong GuildId { get; init; }
}
