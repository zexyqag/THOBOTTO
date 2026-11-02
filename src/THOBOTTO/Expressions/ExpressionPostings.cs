using NetCord.Gateway;

using THOBOTTO.Modules;

namespace THOBOTTO.Expressions;

public sealed class ExpressionPostings : IPostingSource
{
    public Task<IReadOnlyList<Posting>> PostingsAsync(Guild guild)
        => Task.FromResult<IReadOnlyList<Posting>>([new(ExpressionShelf.ModuleId, "Emoji and sticker proposals", new Landing.WhereStarted("where each is proposed (from the panel: only the happenings forum)"), null)]);
}
