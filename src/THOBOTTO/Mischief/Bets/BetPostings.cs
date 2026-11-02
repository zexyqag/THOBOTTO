using NetCord.Gateway;

using THOBOTTO.Modules;

namespace THOBOTTO.Mischief.Bets;

public sealed class BetPostings : IPostingSource
{
    public Task<IReadOnlyList<Posting>> PostingsAsync(Guild guild)
        => Task.FromResult<IReadOnlyList<Posting>>([new(MischiefModule.ModuleId, "Bets", new Landing.WhereStarted("where each is made"), null)]);
}
