namespace THOBOTTO.Helpers;

// Voice channels people only pass through, moved on from at once (a hub, to a channel of their own): no helper comes to
// one on its own.
public interface IPassingChannels
{
    Task<IReadOnlySet<ulong>> InAsync(ulong guildId);
}
