using NetCord.Gateway;

using THOBOTTO.Modules;

namespace THOBOTTO.Overview;

public sealed class OverviewPostings(OverviewBoard board) : IPostingSource
{
    public async Task<IReadOnlyList<Posting>> PostingsAsync(Guild guild)
        => [.. (await board.ListAsync(guild.Id)).Select(w => new Posting(OverviewBoard.ModuleId, w.Title is { Length: > 0 } title ? $"Overview: {title}" : "An overview", new Landing.In(w.ChannelId), $"/g/{guild.Id}/overview"))];
}
