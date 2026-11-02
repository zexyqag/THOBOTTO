using NetCord.Gateway;

using THOBOTTO.Modules;

namespace THOBOTTO.Feeds;

public sealed class FeedPostings(FeedWatch feeds) : IPostingSource
{
    public async Task<IReadOnlyList<Posting>> PostingsAsync(Guild guild)
        => [.. (await feeds.ListAsync(guild.Id)).Select(f => new Posting(FeedWatch.ModuleId, $"News: {f.Title}", new Landing.In(f.ChannelId), $"/g/{guild.Id}/feeds"))];
}
