using NetCord.Gateway;

using THOBOTTO.Modules;

namespace THOBOTTO.Sounds;

public sealed class SoundPostings : IPostingSource
{
    public Task<IReadOnlyList<Posting>> PostingsAsync(Guild guild)
        => Task.FromResult<IReadOnlyList<Posting>>([new(SoundBoard.ModuleId, "Sound proposals", new Landing.WhereStarted("where each is proposed (from the panel: only the happenings forum)"), null)]);
}
