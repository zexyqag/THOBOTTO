using NetCord.Gateway;

using THOBOTTO.Happenings;
using THOBOTTO.Modules;

namespace THOBOTTO.Wrapped;

public sealed class WrappedPostings(SettingsStore settings, HappeningForum forum) : IPostingSource
{
    public async Task<IReadOnlyList<Posting>> PostingsAsync(Guild guild)
    {
        var channel = (await settings.GetAsync<WrappedRules>(guild.Id, WrappedPoster.ModuleId)).ChannelId;
        var setAt = Posting.Settings(guild.Id, WrappedPoster.ModuleId);
        var inForum = await forum.ShowsAsync(guild.Id, WrappedPoster.Happening.Id) ? await forum.ForumOfAsync(guild.Id) : null;
        List<Posting> postings = [];
        if (channel is { } own)
            postings.Add(new(WrappedPoster.ModuleId, "Wrapped", new Landing.In(own), setAt));
        if (inForum is { } posts)
            postings.Add(new(WrappedPoster.ModuleId, "Wrapped", new Landing.In(posts), setAt));
        if (postings.Count == 0)
            postings.Add(new(WrappedPoster.ModuleId, "Wrapped", new Landing.Nowhere("not posted until it has a channel, or the happenings forum shows it"), setAt));
        return postings;
    }
}
