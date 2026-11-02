using NetCord.Gateway;

using THOBOTTO.Modules;

namespace THOBOTTO.Moderation;

public sealed class ModPostings(SettingsStore settings) : IPostingSource
{
    public async Task<IReadOnlyList<Posting>> PostingsAsync(Guild guild)
    {
        var rules = await settings.GetAsync<ModRules>(guild.Id, CaseBook.ModuleId);
        var setAt = Posting.Settings(guild.Id, CaseBook.ModuleId);
        List<Posting> postings =
        [
            new(CaseBook.ModuleId, "Moderation cases", rules.LogChannelId is { } log ? new Landing.In(log) : new Landing.Nowhere("not logged until the log has a channel"), setAt),
            new(CaseBook.ModuleId, "What members report: messages and members", rules.LogChannelId is { } reports ? new Landing.In(reports) : new Landing.Nowhere("members can't report until the log has a channel"), setAt),
        ];
        if (rules.TakeAppeals)
            postings.Add(new(CaseBook.ModuleId, "Ban appeals", rules.AppealsTo() is { } appeals ? new Landing.In(appeals) : new Landing.Nowhere("bans get no Appeal button until appeals or the log have a channel"), setAt));
        return postings;
    }
}
